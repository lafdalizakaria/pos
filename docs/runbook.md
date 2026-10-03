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

## Tests

```bash
dotnet test tests/Newrest.Pos.Domain.Tests                     # unitaires (rapides)
dotnet test tests/Newrest.Pos.Infrastructure.IntegrationTests  # nécessite Docker (SQL Server via Testcontainers)
```

Image SQL Server des tests surchargeable : `POS_TEST_MSSQL_IMAGE`.

## Santé des services

`GET /health/live` (processus) et `GET /health/ready` (base de données) sur l'API et le back-office.
