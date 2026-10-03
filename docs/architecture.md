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

## Tests

| Projet | Contenu |
|---|---|
| `tests/Newrest.Pos.Domain.Tests` | Subvention (plafonds repas/jour multi-sites, forfait, nb repas, sélection de règle), ledger, tickets/avoirs, chaîne, numérotation, Z, seuils vision, prix, menus, PIN/verrouillage, badges |
| `tests/Newrest.Pos.Infrastructure.IntegrationTests` | SQL Server (Testcontainers) : migrations + seed, 50 débits concurrents, idempotence (séquentielle et concurrente), conflit de clé, contre-passation unique, rejeu hors ligne, aller-retour ticket + hash, altération SQL détectée, immutabilité, unicité séquence/session, Z persisté |
