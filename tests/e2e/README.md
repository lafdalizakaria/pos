# Smoke test du back-office (Playwright)

Parcours navigateur sur une base fraîchement initialisée avec les données de démonstration et la connexion de développement :
toutes les pages, émission d'une clé de caisse, recharge de compte, création de menu, badge perdu, périmètre d'un
responsable de site, refus d'accès au journal d'audit.

```bash
# base de dev réinitialisée + back-office lancé en Development (voir docs/runbook.md)
npm install playwright && npx playwright install chromium
node tests/e2e/backoffice-smoke.mjs ./screenshots      # BACKOFFICE_URL=http://127.0.0.1:5090 par défaut
```

Chaque ligne affiche `OK` ou `FAIL`. Le test modifie les données : relancer `Newrest.Pos.Migrator --seed-demo` sur une base vide avant chaque exécution.
