# Architecture

## Vue d'ensemble

```
            ┌──────────────── Site (N caisses) ────────────────┐            ┌────────── Central ──────────┐
 Caméra ──► │ Service vision (FastAPI, 127.0.0.1)              │            │ API REST /api/v1            │
            │        ▲ POST /recognize                         │   HTTPS    │  (ASP.NET Core)             │
 Badge  ──► │ Caisse WPF (MVVM) ── SQLite (cache + outbox) ────┼──────────► │ Back-office Blazor Server   │
 Imprimante │                                                  │  sync      │ SQL Server                  │
            └──────────────────────────────────────────────────┘            └─────────────────────────────┘
```

## Couches (.NET)

| Projet | Rôle | Dépend de |
|---|---|---|
| `Newrest.Pos.Domain` | Entités, invariants et règles pures : subvention, ledger, numérotation, chaînage, Z, seuils vision, prix, PIN. Aucune dépendance externe. | — |
| `Newrest.Pos.Contracts` | Contrats d'API partagés serveur / caisse / vision | — |
| `Newrest.Pos.Application` | Cas d'usage et interfaces (ex. `IAccountLedger`) | Domain, Contracts |
| `Newrest.Pos.Infrastructure` | EF Core SQL Server, intercepteurs, ledger SQL, seed démo | Application |
| `Newrest.Pos.Migrator` | Outil dédié d'application des migrations (+ seed démo, script SQL idempotent) | Infrastructure |
| `Newrest.Pos.Api` | API REST centrale, health checks, OpenTelemetry | Infrastructure |
| `Newrest.Pos.BackOffice` | Back-office Blazor Server (FR, RTL prévu) | Infrastructure |
| `Newrest.Pos.Client` | Caisse WPF (`net10.0-windows`, compilable sous Linux pour la CI) | Client.Data, Devices |
| `Newrest.Pos.Client.Data` | SQLite local, cache, outbox | Domain, Contracts |
| `Newrest.Pos.Devices` | Abstractions périphériques + simulateurs | — |

## Décisions clés (phase 1)

### Identifiants
GUID v7 générés par le domaine (`Entity`). Les caisses créent tickets, paiements et mouvements **hors ligne** ; le serveur
n'attribue jamais d'identifiant. L'identifiant du ticket sert aussi de clé d'idempotence de synchronisation.

### Ledger des comptes
- `AccountMovement` est immuable ; le solde = Σ mouvements. `Account.CachedBalance` est mis à jour dans la même
  transaction que l'insertion (et vérifiable : `IAccountLedger.FindCacheDriftAsync`).
- Concurrence : transaction courte avec `SELECT … WITH (UPDLOCK, ROWLOCK)` sur la ligne du compte → contrôle de solde,
  insertion, mise à jour du cache. Les débits d'un même compte sont sérialisés, les autres comptes ne sont pas bloqués.
  Le `rowversion` du compte est un second filet de sécurité. Testé : 50 débits simultanés → exactement 25 acceptés
  (250 MAD disponibles, 10 MAD chacun), solde final = −découvert.
- Idempotence : index unique sur `IdempotencyKey` ; un rejeu renvoie le mouvement d'origine (`WasDuplicate`) ; une même
  clé avec un autre montant/compte lève `IdempotencyConflictException`. Une clé est revérifiée après obtention du verrou.
- Correction = contre-passation (`Correction` référencée par `ReversesMovementId`, index unique filtré → une seule fois).

### Tickets et chaîne d'intégrité
- `Ticket.Issue` / `Ticket.IssueCreditNote` construisent un ticket **scellé** : totaux, TVA, paiements validés
  (Σ paiements = part convive ; TTC = subvention + part convive, aussi en contrainte CHECK SQL), puis hash.
- `TicketHasher` : JSON canonique versionné → SHA-256. `TicketChainVerifier` détecte trous, doublons, liens rompus,
  contenus altérés, tickets d'une autre caisse, y compris à partir d'un point de contrôle.
- Base : index unique (`RegisterId`, `Sequence`) et sur `Number` ; un avoir au plus par ticket.

### Immutabilité
Marqueur `IImmutableRecord` (Ticket, TicketLine, Payment, AccountMovement, ZReport, ZReportLine, AuditLog) :
`PosSaveChangesInterceptor` lève `ImmutableRecordException` sur toute modification/suppression. Défense en profondeur :
`DENY UPDATE, DELETE` pour le compte SQL applicatif (`deploy/sql/least-privilege.sql`).

### Synchronisation descendante
Toutes les données de référence (`ReferenceEntity`) portent un `rowversion` indexé, curseur de synchronisation
incrémentale (phase 3), ainsi que `CreatedAt` / `UpdatedAt`.

### Schéma SQL Server
Schéma `pos`, énumérations en texte, montants `decimal(18,2)`, taux `decimal(9,4)`, pas de cascade sur les données
métier, migrations EF Core appliquées **uniquement** par `Newrest.Pos.Migrator` (jamais au démarrage de l'API).

## Phase 2 — API centrale et back-office

### Cas d'usage partagés
`Newrest.Pos.Application` contient les services (`OrganizationService`, `CatalogService`, `MenuService`, `ClientService`,
`DinerService`, `AccountService`, `AuditQueryService`, `RegisterAuthService`) utilisés **à la fois** par l'API REST et
par le back-office Blazor : une seule implémentation des règles, des droits et de l'audit. Ils travaillent sur
`IPosDbContext` (implémenté par `PosDbContext`) et `ICurrentUser` (fourni par chaque hôte).

### Authentification
| Appelant | Mécanisme |
|---|---|
| Utilisateur du back-office (navigateur) | Entra ID OpenID Connect (code + PKCE) → cookie ; rôles = rôles d'application Entra (`roles`) |
| Appel API par un utilisateur | Jeton d'accès Entra ID (JWT) validé par l'API (`Authentication:Users:Authority/Audience`) |
| Caisse | Clé d'appareil (`nrpos_…`, SHA-256 stocké) échangée sur `POST /api/v1/auth/register-token` contre un JWT HS256 de 15 min (`register_id`, `pos_id`, `site_id`, `company_id`, rôle `Pos.Register`) |
| Développement / tests | Connexion locale (back-office) et jetons HS256 de test (API), **refusés en Production** |

L'API choisit le schéma de validation d'après l'émetteur du jeton (schéma « sélecteur »), puis valide signature,
audience, durée de vie et algorithme dans le schéma cible.

### Autorisation
1. Politique d'endpoint : tout `/api/v1` (hors `ping` et `auth/register-token`) exige un rôle back-office.
2. `AccessControl` (Application) : rôle requis par action + périmètre société/site (`UserAccessScopes`).
   Lecture : périmètre ; gestion société (sites, clients, contrats, listes de prix société) : périmètre société entière ;
   catalogue maître et droits : administrateurs.
3. Le back-office masque les actions non autorisées, mais la vérification se fait toujours côté cas d'usage.

### Back-office Blazor Server
Rendu interactif sans pré-rendu. Chaque opération s'exécute dans **son propre scope DI** (`BackOfficeRunner`) avec
l'identité du circuit : un `DbContext` neuf par opération (pas de contexte partagé pendant toute la durée d'un circuit).
Les erreurs métier (`DomainException.Code`) sont traduites en messages français (`Ui/Messages.cs`).

### Erreurs API
RFC 9457 (`application/problem+json`) avec extension `code` stable : 404 `not_found`, 403 `forbidden`,
409 (`conflict`, `idempotency_conflict`, `already_reversed`…), 422 pour les autres règles métier.

## Phase 3 — Caisse et synchronisation

```
 WPF (XAML, DPAPI, second écran, lecteur clavier)          ← Newrest.Pos.Client (Windows)
   └── ViewModels MVVM + services de caisse                 ← Newrest.Pos.Client.Core (multiplateforme, testé)
         ├── SQLite : cache de référence, état fiscal, tickets, mouvements, Z, outbox   ← Newrest.Pos.Client.Data
         ├── Périphériques : ESC/POS, badge, afficheur, TPE, caméra (+ simulateurs)    ← Newrest.Pos.Devices
         └── PosApiClient ──HTTPS──► /api/v1/register/* (jeton caisse)                  ← Newrest.Pos.Api
```

### Vente
1. Badge → contexte en ligne (`GET register/badges/{n}` : solde frais, subvention déjà accordée aujourd'hui sur **tous**
   les points de vente) ou, en cas d'échec réseau, contexte hors ligne (cache SQLite + tickets locaux).
2. Subvention calculée par le Domain (`SubsidyCalculator`).
3. Paiement compte : débit en ligne sur le ledger (solde imposé) ; hors ligne, contrôle local + plafond par badge.
4. **Une transaction SQLite** : séquence suivante, ticket scellé (empreinte chaînée), débit hors ligne puis ticket
   ajoutés à l'outbox, état fiscal mis à jour. Impossible de perdre un ticket ou de créer un trou, même en cas de coupure.
5. Impression (une panne n'annule pas la vente), afficheur client, déclenchement de la synchronisation.

### Synchronisation
- **Montante** : outbox envoyée dans l'ordre (session → mouvements → tickets → Z), chaque élément idempotent.
  Le serveur exige l'ordre des séquences, l'enchaînement des empreintes, la présence du mouvement de chaque paiement
  compte, et reconstruit chaque ticket pour vérifier l'empreinte. Un refus définitif bloque la file (affiché en rouge).
- **Descendante** : `GET register/reference?cursor=…` incrémental (`rowversion`), instantané complet périodique.
- **Indicateur permanent** : en ligne / hors ligne / nombre d'éléments en attente / file bloquée.

### Clôture Z
Calculée en caisse, envoyée après tous les tickets de la session ; le serveur la recalcule et refuse un écart.

## Phase 4 — Reconnaissance des plateaux

```
 Caméra USB ──► Service vision (Python, 127.0.0.1:8765, service Windows)      ← vision/
                 GET  /camera/capture  (OpenCV, exposition fixe, recadrage plateau)
                 POST /recognize       image + candidats (+ photos de référence) → provider → codes du menu uniquement
                 POST /feedback        lignes validées → labels YOLO du dataset local
                 providers : mock │ gemini │ yolo, hybrid (phase 5) — choisis par vision.json, relu à chaud
                        ▲
 Caisse (Client.Core) ──┘ TrayRecognitionService : budget 6 s, seuils 0,60/0,90, jamais bloquant
                        └─ outbox « Recognition » ──► POST /api/v1/register/recognitions ──► RecognitionLog
                                                                      Back-office « Performance vision »
```

### Flux d'une vente assistée
1. **Photo plateau** (bouton ou F2) : la caisse demande l'image au service (`VisionServiceCamera`), puis
   `POST /recognize` avec les articles du menu affiché (code, libellé, catégorie, description visuelle) et les photos de
   référence en cache. Tout est borné à 6 s ; toute erreur (caméra, service, modèle, délai, visage) affiche un message
   et laisse la saisie manuelle.
2. Le service garde-fou : image décodée et réduite (1024 px), refus si visage, appel du provider sous délai, codes
   hors candidats écartés, boîtes ramenées en pixels de l'image d'origine + format YOLO, enregistrement dans le dataset.
3. La caisse classe chaque proposition (`RecognitionConfidencePolicy`) : ajout automatique, ligne en surbrillance avec
   second choix, ou simple indication de catégorie.
4. Après encaissement : les lignes validées, rattachées à leur prédiction, partent au service (`/feedback`, labels du
   dataset) et au serveur via l'outbox (statistiques). Les lignes du ticket portent leur origine (`LineSource`).

### Choix
- La caméra est lue par le service Python (OpenCV y est déjà) : la caisse .NET n'embarque aucune bibliothèque
  d'imagerie native.
- Les photos de référence passent en parties fichier `reference_<CODE>` (champs de formulaire limités à 1 Mo par
  Starlette), réduites à 512 px par le service.
- Les statistiques ne bloquent jamais la file fiscale : un refus serveur d'un élément `Recognition` est journalisé et
  sauté.

## Phase 5 — Modèles YOLO et déploiement

```
 Caisses : dataset local (images + lignes validées) ──(URL signée, optionnel)──► stockage d'entraînement
                                                                                     │
 Machine d'entraînement : python -m training annotate │ build │ train ◄──────────────┘
        └─► model.onnx + manifest.json (classes, mAP50, SHA-256)
                 │ import (empreinte vérifiée)            Back-office « Modèles vision »
                 ▼                                        brouillon → publié → retiré ; réglages par site
 API centrale : VisionModels, SiteVisionSettings, RegisterVisionStatuses
                 │ GET register/vision (toutes les 5 min, hors file fiscale)
                 ▼
 Caisse : VisionDeploymentService ── téléchargement + SHA-256 ──► PUT /models/{v} (service : SHA-256 + chargement ONNX)
                                   └─ seuils du site → écran de vente          PUT /runtime (moteur + modèle)
                                   └─ POST register/vision/status ──► état des caisses en back-office
```

- **Providers** : `yolo` (ONNX Runtime, sortie `[1, 4+classes, N]`, classes hors menu masquées, suppression des
  doublons toutes classes confondues) ; `hybrid` (YOLO puis Gemini si doute ou article inconnu du modèle, fusion par
  boîte, repli sur YOLO si Gemini échoue).
- **Configuration du service en couches** : environnement < `runtime.json` (réglages du site poussés par la caisse)
  < `vision.json` (secours local, prioritaire). Relue à chaque requête : aucun redémarrage.
- **Retour arrière** : les 3 dernières versions restent installées ; réaffecter une version au site suffit.
- **Calibration** : `ThresholdAdvisor` (Domain) compare les confiances aux validations des caissières et suggère les
  seuils (page *Performance vision*, filtrable par moteur/modèle).

## Tests

| Projet | Contenu |
|---|---|
| `tests/Newrest.Pos.Domain.Tests` | Subvention (plafonds repas/jour multi-sites, forfait, nb repas, sélection de règle), ledger, tickets/avoirs, chaîne, numérotation, Z, seuils vision, prix, menus, PIN/verrouillage, badges |
| `tests/Newrest.Pos.Api.Tests` | API complète (WebApplicationFactory + SQL Server) : authentification (jetons invalides/expirés, clé de caisse, rotation, révocation, limitation de débit), périmètres et rôles, organisation, catalogue, photos, tarifs, menus (copie jour/semaine), clients, contrats, subventions, convives, badges perdus, import CSV/Excel, comptes (recharge idempotente, corrections, contre-passations, remboursements), audit |
| `tests/Newrest.Pos.Devices.Tests` | ESC/POS (octets, accents, coupe, tiroir, TCP/fichier), détection du lecteur de badge clavier, simulateurs |
| `tests/Newrest.Pos.Scenarios.Tests` | Caisse complète sans interface (Client.Core + SQLite + simulateurs) contre l'API réelle sur SQL Server : vente badge avec subvention → impression → synchronisation → back-office ; **20 ventes hors ligne** avec plafond atteint, retour réseau avec réponses perdues, sans doublon ; avoir + recharge + Z ; badge perdu ; caisse réinstallée ; file bloquée puis relancée ; écran de vente en 3 gestes |
| `tests/Newrest.Pos.Scenarios.Tests` (vision) | Vente assistée (seuils, second choix, catégorie, retour d'apprentissage, statistiques serveur, < 10 s), service lent (repli manuel), service arrêté/en erreur, nouvelle photo, photos de référence (téléchargement, envoi, synchronisation incrémentale), client HTTP vision ; **caisse + vrai service Python** (mock) jusqu'au dataset |
| `tests/Newrest.Pos.Scenarios.Tests` (déploiement) | Réglages du site → téléchargement, contrôle d'empreinte, installation, bascule, seuils appliqués, état remonté ; pas de re-téléchargement ; retour à Gemini et retour arrière ; « dernier publié » vs modèle fixé ; **modèle altéré refusé** ; site désactivé ; service injoignable ; secours local ; **back-office → caisse → vrai service Python avec un modèle ONNX → vente** |
| `vision/tests` (pytest, phase 5) | Décodage YOLO (letterbox, masque du menu, suppression des doublons, alternatives), stockage des modèles (empreinte, forme, conflit, purge), `/models` et `/runtime`, secours local, hybride (fusion, nouvel article, Gemini lent/en panne), dataset d'entraînement, annotation ; `-m training` : **entraînement réel** + export ONNX + installation + reconnaissance |
| `vision/tests` (pytest) | Contrat de réponse, rejet des codes hors menu, délai dépassé (504), erreurs provider, images invalides, garde-fou visages, dataset/feedback/labels YOLO, envoi central, schéma et requête Gemini (faux client), clé DPAPI, caméra (recadrage), bascule de provider à chaud, écoute locale uniquement |
| `tests/e2e/backoffice-smoke.mjs` | Parcours navigateur (Playwright) du back-office, hors CI |
| `tests/Newrest.Pos.Infrastructure.IntegrationTests` | SQL Server (Testcontainers) : migrations + seed, 50 débits concurrents, idempotence (séquentielle et concurrente), conflit de clé, contre-passation unique, rejeu hors ligne, aller-retour ticket + hash, altération SQL détectée, immutabilité, unicité séquence/session, Z persisté |
