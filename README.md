# Newrest POS — caisse multi-site avec reconnaissance des plateaux

Solution de caisse pour Newrest Maroc (restauration collective B2B et retail, multi-sociétés, multi-sites) :
serveur central ASP.NET Core + SQL Server, back-office Blazor Server, caisse WPF hors ligne, service vision local
(FastAPI, YOLO / Gemini) qui pré-remplit le ticket — la caissière valide toujours.

**État : phase 1 (fondations) terminée** — voir [`docs/progress.md`](docs/progress.md).

## Démarrage rapide

```bash
dotnet build Newrest.Pos.slnx                                   # SDK .NET 10
dotnet test tests/Newrest.Pos.Domain.Tests                      # tests unitaires
dotnet test tests/Newrest.Pos.Infrastructure.IntegrationTests   # nécessite Docker
```

Base locale, migrations et données de démonstration : [`docs/runbook.md`](docs/runbook.md).

## Structure

```
src/
  Newrest.Pos.Domain/          entités et règles métier pures (subvention, ledger, numérotation, chaînage, Z)
  Newrest.Pos.Application/     cas d'usage, interfaces
  Newrest.Pos.Infrastructure/  EF Core SQL Server, ledger SQL, seed démo, migrations
  Newrest.Pos.Migrator/        outil d'application des migrations (jamais au démarrage de l'API)
  Newrest.Pos.Api/             API REST centrale /api/v1
  Newrest.Pos.BackOffice/      back-office Blazor Server
  Newrest.Pos.Client/          caisse WPF (MVVM)
  Newrest.Pos.Client.Data/     SQLite local, outbox
  Newrest.Pos.Devices/         périphériques + simulateurs
  Newrest.Pos.Contracts/       contrats d'API partagés
vision/                        service vision Python (phase 4)
tests/                         tests .NET
deploy/                        compose de dev, scripts SQL, CI
docs/                          architecture, hypothèses, conformité, données personnelles, runbook, avancement
```

## Documentation

- [Architecture](docs/architecture.md)
- [Hypothèses et valeurs par défaut](docs/assumptions.md)
- [Conformité fiscale — points à valider](docs/compliance.md)
- [Données personnelles (CNDP)](docs/privacy.md)
- [Runbook](docs/runbook.md)
- [Avancement](docs/progress.md)

Aucun secret dans le dépôt : configuration par `appsettings.*.json` sans secret, variables d'environnement et `.env` (voir `.env.example`).
