# Runbook d'exploitation

> Version phase 1 : environnement de développement et base de données. Les procédures de site (ouverture d'un site,
> ajout d'une caisse, badge perdu, panne réseau, clôture forcée, restauration) seront complétées au fil des phases
> 2 à 6.

## Prérequis développeur

- SDK .NET 10 (`global.json`), Docker (SQL Server et tests d'intégration), PowerShell ou bash.
- Windows pour exécuter la caisse WPF (elle compile aussi sous Linux/macOS).

## Base de données locale

```bash
cp .env.example .env                      # renseigner MSSQL_SA_PASSWORD et ConnectionStrings__PosDb
docker compose -f deploy/docker-compose.dev.yml --env-file .env up -d
dotnet tool restore
export $(grep -v '^#' .env | xargs)       # PowerShell : définir $env:ConnectionStrings__PosDb
DOTNET_ENVIRONMENT=Development dotnet run --project src/Newrest.Pos.Migrator -- --seed-demo
```

## Migrations

| Action | Commande |
|---|---|
| Créer une migration | `dotnet ef migrations add <Nom> -p src/Newrest.Pos.Infrastructure -s src/Newrest.Pos.Infrastructure -o Persistence/Migrations` |
| Appliquer | `dotnet run --project src/Newrest.Pos.Migrator` (ou l'exécutable publié) |
| Lister appliquées / en attente | `… Newrest.Pos.Migrator -- --list` |
| Script SQL idempotent pour le DBA | `… Newrest.Pos.Migrator -- --script migrations.sql` |
| Données de démonstration | `… Newrest.Pos.Migrator -- --seed-demo` (refusé si `DOTNET_ENVIRONMENT=Production`) |

L'API et le back-office **n'appliquent jamais** les migrations au démarrage. En production, le migrateur s'exécute avec
le compte `pos_migrator` ; l'API avec `pos_api` (voir `deploy/sql/least-privilege.sql`).

## Données de démonstration

| Élément | Valeur |
|---|---|
| Sociétés | NFMS (sites Casablanca Sidi Maârouf, Tanger Free Zone), NMS (site Kénitra Atlantic Free Zone) |
| Points de vente | CAS-SELF (2 caisses), CAS-CAFET, TNG-SELF, TNG-SNACK, KEN-SELF |
| Opérateurs | `CAIS01`, `CAIS02` (caissiers, PIN `1234`), `RESP01` (responsable, PIN `5678`) |
| Clients fictifs | ATLAS (50 % plafonné 20 MAD/jour ; cadres 25 MAD), SAHARA (15 MAD, 1 repas/jour) |
| Badges | `BDG-ATL0001` … `BDG-ATL0005`, `BDG-SAH0001` … `BDG-SAH0003` |

Les PIN de démonstration sont publics : ne jamais lancer `--seed-demo` sur un environnement partagé sans les changer.

## Back-office et API en local

```bash
# back-office : connexion de développement (aucun Entra ID requis)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Newrest.Pos.BackOffice
# API : la clé de signature des jetons caisse est générée à la volée en Development (jetons perdus au redémarrage)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Newrest.Pos.Api
```

Sur la page de connexion de développement, saisir un UPN et cocher les rôles. Un utilisateur non administrateur doit
recevoir un périmètre (menu **Droits d'accès**, en tant qu'administrateur).

## Configuration Entra ID (production)

1. **Inscription d'application « Newrest POS »** (API + back-office) : exposer une API (`api://newrest-pos`), créer les
   rôles d'application `Pos.Admin`, `Pos.Manager`, `Pos.Accountant`, `Pos.Viewer` et les attribuer aux utilisateurs/groupes.
2. Back-office : URI de redirection `https://<hôte>/signin-oidc`, secret client dans le coffre ;
   `Authentication__Authority`, `Authentication__ClientId`, `Authentication__ClientSecret`.
3. API : `Authentication__Users__Authority` (`https://login.microsoftonline.com/<tenant>/v2.0`), `Authentication__Users__Audience`.
4. API : `Authentication__Registers__SigningKey` (≥ 32 caractères aléatoires, coffre de secrets).
5. Donner les périmètres (société / site) dans **Droits d'accès** à chaque utilisateur non administrateur.

## Ajouter une caisse

1. Back-office → **Points de vente & caisses** → choisir le site et le point de vente → **Nouvelle caisse** (code,
   nom, **préfixe de ticket unique et définitif**, ex. `CAS4`).
2. **Émettre une clé** : la clé `nrpos_…` s'affiche **une seule fois** ; la saisir dans l'installation de la caisse (phase 3).
3. Une clé perdue ou un poste remplacé : **Renouveler la clé** (l'ancienne cesse immédiatement de fonctionner) ou **Révoquer**.

## Badge perdu

Back-office → **Convives & badges** → rechercher le convive (nom, matricule ou n° de badge) → **Déclarer perdu** →
saisir le numéro du nouveau badge. L'ancien badge est bloqué immédiatement, le solde reste sur le compte. Les caisses
reçoivent le blocage à la prochaine synchronisation (5 min, ou bouton **Synchroniser**).

## Installer une caisse (poste Windows)

1. Back-office : créer la caisse et **émettre une clé** (voir « Ajouter une caisse »), noter l'identifiant de la caisse.
2. Poste : publier/copier `Newrest.Pos.Caisse` (installeur MSIX en phase 6) ; renseigner `appsettings.json` :
   `Register:ServerUrl` (HTTPS), `Register:DataFolder` (par défaut `C:\ProgramData\Newrest\POS`), périphériques
   (`Devices:Printer` = `EscPosSpooler` + nom de l'imprimante Windows, ou `EscPosTcp` + `ip:9100` ; `Devices:BadgeReader`
   = `Keyboard` ou `Serial` + port ; `Devices:CustomerDisplay` = `Window` si second écran à droite).
3. Premier lancement : écran **Enregistrement de la caisse** → adresse du serveur, identifiant, clé. La clé est chiffrée
   par DPAPI sur le poste ; les données de référence sont téléchargées.
4. Connexion opérateur (code + PIN), ouverture de caisse (fond), vente.

## Panne réseau

- La caisse continue : catalogue, menus, badges et soldes sont en cache ; bandeau rouge « Hors ligne — n en attente ».
- Paiement par compte limité au plafond hors ligne par badge (60 MAD par défaut) ; au-delà, espèces ou carte.
- Au retour du réseau, la file se vide automatiquement (toutes les 10 s, ou bouton **Synchroniser**) ; aucun doublon
  possible (idempotence serveur).
- Contrôle : back-office → **Clôtures Z & intégrité** → *Vérifier* la caisse.

## File de synchronisation bloquée (bandeau « Synchronisation bloquée »)

Un élément a été refusé par le serveur (caisse désactivée, empreinte invalide…). Les éléments suivants attendent
(ordre fiscal). Lire l'erreur dans le journal de la caisse (`logs/caisse-*.log`), corriger la cause (ex. réactiver la
caisse), puis un responsable relance l'envoi (`RetryRejected`, bouton à exposer dans l'écran responsable en phase 6).
Ne **jamais** supprimer la base SQLite d'une caisse dont la file n'est pas vide.

## Avoir

Caisse → **Historique** → sélectionner le ticket → motif → **Avoir (responsable)** (opérateur Responsable connecté).
Avoir total, numéroté dans la séquence de la caisse, remboursé par les mêmes moyens (compte recrédité).

## Clôture de caisse

Caisse → **Clôture** → compter les espèces → *Calculer l'écart* → **Clôturer et imprimer le Z**. Le Z est envoyé au
serveur après tous les tickets ; le serveur le recalcule. Une nouvelle session peut ensuite être ouverte.

## Tests

```bash
dotnet test tests/Newrest.Pos.Domain.Tests                     # unitaires (rapides)
dotnet test tests/Newrest.Pos.Infrastructure.IntegrationTests  # nécessite Docker (SQL Server via Testcontainers)
dotnet test tests/Newrest.Pos.Api.Tests                         # API complète, nécessite Docker
dotnet test tests/Newrest.Pos.Devices.Tests                     # périphériques
dotnet test tests/Newrest.Pos.Scenarios.Tests                   # caisse de bout en bout (hors ligne inclus), nécessite Docker
node tests/e2e/backoffice-smoke.mjs                             # parcours navigateur, voir tests/e2e/README.md
```

Image SQL Server des tests surchargeable : `POS_TEST_MSSQL_IMAGE`.

## Santé des services

`GET /health/live` (processus) et `GET /health/ready` (base de données) sur l'API et le back-office.
