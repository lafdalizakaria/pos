# Avancement

## Phase 1 — Fondations ✅ (validée)

### Livré

**Solution et outillage**
- Solution `Newrest.Pos.slnx` (.NET 10 LTS), 10 projets `src/` + 2 projets de tests, gestion centralisée des versions
  NuGet (`Directory.Packages.props`), analyse .NET + style imposés au build, avertissements bloquants en CI.
- La caisse WPF (`net10.0-windows`) compile aussi sous Linux (`EnableWindowsTargeting`) : toute la solution est
  construite en CI sur `ubuntu-latest`.
- CI GitHub Actions (`.github/workflows/ci.yml`) : restore, `dotnet format --verify-no-changes`, build Release,
  vérification que le modèle EF correspond aux migrations, tests unitaires avec couverture, tests d'intégration
  SQL Server (Testcontainers), seuil de couverture Domain ≥ 80 %. CodeQL (C#) dans `codeql.yml`.
- `.env.example`, `deploy/docker-compose.dev.yml` (SQL Server de dev), `deploy/sql/least-privilege.sql`.

**Domain** (`Newrest.Pos.Domain`, sans dépendance)
- Organisation : `Company`, `Site`, `PointOfSale`, `Register`, `Operator` (PIN PBKDF2-SHA256 600 k itérations,
  verrouillage après échecs).
- Catalogue : `Category`, `Article` (TVA, photos de référence, description visuelle, subventionnable), `PriceList` +
  `PriceOverride` (société / site / point de vente, validité) et `PriceResolver`.
- Menus : `DailyMenu` / `DailyMenuItem` (disponibilité, publication, copie vers une autre date).
- Clients : `ClientCompany`, `Contract` (points de vente acceptés), `SubsidyRule`, `Diner`, `Badge` (perdu → remplacé,
  le solde reste au compte).
- Ledger : `Account` (prépayé / postpayé / mixte, découvert, cache), `AccountMovement` immuable, contre-passation.
- Subvention : `SubsidyCalculator` (%, forfait, plafond repas, plafond jour multi-points de vente, nb repas/jour,
  sélection de règle par catégorie). Exemple de référence vérifié : 45 MAD, 50 % plafonné 20 MAD/jour → 20 / 25.
- Ventes : `Ticket` scellé (ventes, avoirs totaux), `TicketLine`, `Payment` (espèces + rendu, TPE + référence
  d'autorisation, compte, mixte), numérotation `TicketNumber`, chaîne `TicketHasher` / `TicketChainVerifier`,
  `CashSession`, `ZReport` + `ZReportCalculator` (moyens de paiement, TVA, subventions, recharges hors CA, écart).
- Vision : `RecognitionConfidencePolicy` (0,60 / 0,90), `RecognitionLog`. Audit : `AuditLog`.

**Infrastructure**
- `PosDbContext` (schéma `pos`), migration `InitialCreate`, index uniques métier (séquence par caisse, idempotence,
  contre-passation unique, une session ouverte par caisse…), contraintes CHECK, `rowversion` sur les données de
  référence (curseur de sync), aucune cascade sur les données métier.
- `PosSaveChangesInterceptor` : horodatages techniques, refus de toute modification/suppression d'un enregistrement immuable.
- `AccountLedger` (`IAccountLedger`) : débit atomique sous `UPDLOCK, ROWLOCK`, idempotence, détection de conflit de clé,
  contre-passation unique, rejeu hors ligne marqué, contrôle de dérive du cache.
- `DemoDataSeeder` : 2 sociétés, 3 sites, 5 points de vente, 6 caisses, 5 opérateurs, 30 articles (dont couscous
  viande `CSC-VND` et couscous poulet `CSC-PLT`), 2 listes de prix, menus du jour publiés, 2 clients fictifs, contrats,
  3 règles de subvention, 8 convives avec badges et comptes approvisionnés. Idempotent.
- `Newrest.Pos.Migrator` : applique les migrations, `--seed-demo` (refusé en Production), `--list`, `--script`.
- Squelettes hébergés : API (`/health/live`, `/health/ready`, `/api/v1/ping`, OpenAPI, Serilog, OpenTelemetry),
  back-office Blazor Server en français, caisse WPF MVVM (fenêtre d'accueil).

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| `dotnet build` solution complète, `CI=true` (avertissements = erreurs) | ✅ 0 avertissement |
| `dotnet format --verify-no-changes` | ✅ |
| Tests unitaires Domain | ✅ 96/96 |
| Tests d'intégration SQL Server 2022 (Testcontainers) | ✅ 13/13 dont **50 débits concurrents** (25 acceptés exactement, solde = −découvert, cache cohérent) et 20 soumissions simultanées d'une même clé (1 seul mouvement) |
| Couverture lignes Domain | ✅ 90,1 % en Release (seuil 80 %) |
| Modèle EF ↔ migrations | ✅ aucune modification en attente |
| Migrateur + seed sur SQL Server réel, re-seed idempotent, script SQL idempotent | ✅ |
| API et back-office démarrés : `/health/ready` = Healthy, `/api/v1/ping`, `/openapi/v1.json`, page d'accueil FR | ✅ |
| `least-privilege.sql` sur SQL Server réel : `pos_api` peut mettre à jour `Accounts`, reçoit un refus `UPDATE`/`DELETE` sur `AccountMovements`, `Tickets`, `ZReportLines`, et n'a aucun droit DDL | ✅ |
| Workflows GitHub Actions | ⚠️ écrits mais pas encore exécutés sur GitHub (pas de push effectué) |

### Décisions prises
Voir `docs/assumptions.md` (A1–A30) et `docs/architecture.md`. Points saillants :
- Verrou pessimiste `UPDLOCK, ROWLOCK` (plutôt que retry optimiste) pour le débit : plus simple à raisonner, sérialise
  uniquement le compte concerné ; `rowversion` conservé en filet de sécurité.
- La subvention est portée par le ticket ; seul le reste à charge est débité du compte.
- Une vente hors ligne déjà réalisée est acceptée à la synchronisation même au-delà du découvert, mais marquée
  (`ExceededOverdraft`) — **à valider par le métier/finance** (A18).
- Séquence de tickets partagée ventes/avoirs ; avoir total uniquement en phase 1 (A6, A7).
- FluentAssertions figé en 7.x pour éviter la licence commerciale de la v8 (A29).

### Reste à faire / points ouverts
- Questions fiscales listées dans `docs/compliance.md` (mentions obligatoires, taux de TVA, traitement de la subvention,
  conservation, éventuelle certification du logiciel de caisse).
- `Newrest.Pos.Application` ne contient encore que l'interface du ledger : les cas d'usage arrivent en phase 2 ;
  le seuil de couverture 80 % lui sera appliqué dès qu'il contiendra du code.
- Job CI Python ajouté en phase 4 avec le service vision.

## Phase 2 — API centrale + back-office ✅ (validée)

### Livré

**Cas d'usage partagés** (`Newrest.Pos.Application`, utilisés par l'API et le back-office)
- `AccessControl` : rôles Entra ID (`Pos.Admin`, `Pos.Manager`, `Pos.Accountant`, `Pos.Viewer`) + périmètres société/site
  (`UserAccessScopes`, nouvelle table). `AuditTrail` : audit dans la même transaction que la modification.
- Organisation : sociétés, sites, points de vente, caisses (préfixe immuable, **clé d'appareil** affichée une fois,
  rotation, révocation), opérateurs (PIN haché, rôles, déverrouillage), droits d'accès.
- Catalogue : catégories, articles (code immuable, prix/TVA audités), **photos de référence** (JPEG/PNG/WebP, 5 Mo,
  `IFileStorage` sur disque), listes de prix par société / site / point de vente, prix effectifs d'un point de vente.
- Menus du jour : création avec prix résolus, ajout/retrait/prix/disponibilité, publication, **copie vers d'autres dates**
  et **copie de semaine type**.
- Clients : clients B2B, contrats (points de vente acceptés), règles de subvention (création, clôture — jamais modifiées).
- Convives : fiche, badges (attribution, **badge perdu → remplacement**, blocage), recherche par badge, **import CSV / Excel**
  (simulation, rapport d'erreurs par ligne, aucune modification partielle d'une ligne rejetée).
- Comptes : liste, solde, paramètres (type, découvert), historique paginé, **recharge** (idempotente), **correction**,
  **contre-passation**, **remboursement** — tous via le ledger verrouillé et audités.
- Journal d'audit consultable (finance / administrateurs, filtré par périmètre).

**API REST** `/api/v1` : 83 endpoints, OpenAPI, ProblemDetails avec `code` stable, authentification Entra ID (JWT) pour
les utilisateurs et **jetons caisse** (clé d'appareil → JWT 15 min, limitation 20 req/min/IP), sélection du schéma
par émetteur.

**Back-office Blazor Server** (français, mise en page prête pour le RTL) : 13 écrans — accueil, sociétés & sites,
points de vente & caisses, opérateurs, articles (photos), catégories, tarifs, menus (vue semaine), clients & contrats &
subventions, convives & badges & import, comptes & opérations, journal d'audit, droits d'accès. Connexion Entra ID
(OpenID Connect) ou connexion de développement (refusée en Production). Chaque opération utilise un `DbContext` neuf.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build complet Release, avertissements = erreurs, `dotnet format` | ✅ |
| Modèle EF ↔ migrations (nouvelle migration `AddUserAccessScopesAndDeviceKeys`) | ✅ |
| Tests unitaires Domain | ✅ 103/103 |
| Tests d'intégration SQL Server | ✅ 13/13 |
| Tests API (WebApplicationFactory + SQL Server 2022) | ✅ 24/24 — jetons absents/forgés/expirés, utilisateur sans rôle, clé caisse (échange, rotation, révocation, autre caisse), limitation de débit, cloisonnement site/société, rôles en écriture, organisation, opérateurs (PIN jamais audité), catalogue + audit des prix, photos, tarifs, menus (copie jour/semaine/écrasement), contrats, subventions, badges perdus, import CSV (dont champs entre guillemets) et Excel, recharge idempotente, conflit de clé, corrections/contre-passations/remboursements |
| Couverture lignes | ✅ Domain 90,3 %, Application 94,1 % (seuils CI 80 % chacun) |
| Parcours navigateur réel (Playwright, `tests/e2e`) sur SQL Server + back-office lancé | ✅ 24/24 : toutes les pages, clé de caisse, recharge, création de menu et ajout d'article, modification de prix, badge perdu, upload de photo, import CSV avec erreur de ligne, responsable de site limité à son site, refus de l'audit |
| Connexion Entra ID réelle | ⚠️ non testée (aucun tenant disponible) : configuration documentée dans `docs/runbook.md`, à valider avec la DSI |

Bugs trouvés et corrigés pendant la vérification : message de confirmation effacé après rechargement, page « accès
refusé » manquante (404), prix des menus non affichés (virgule décimale dans un champ numérique), type MIME des photos,
modification partielle possible sur une ligne d'import rejetée, dépendance du migrateur aux services applicatifs.

### Décisions prises
Voir `docs/assumptions.md` A31–A48. Points saillants :
- Rôles dans Entra ID, **périmètres dans l'application** (administrables sans DSI) ; clients/convives/comptes cloisonnés par société.
- Le back-office appelle directement les cas d'usage (même processus) au lieu de l'API HTTP : une seule implémentation
  des droits et de l'audit, pas de double authentification.
- Une consommation ne se contre-passe pas : elle s'annule par un avoir (phase 3).

### Reste à faire / points ouverts
- Valider la connexion Entra ID sur le tenant Newrest (inscription d'application, rôles d'application).
- Écrans tickets / avoirs / clôtures / vérification de chaîne, facturation mensuelle et tableau de bord : ils
  dépendent des tickets synchronisés par les caisses → livrés avec les phases 3 et suivantes.
- Synchronisation descendante pour les caisses (catalogue, menus publiés, badges, soldes) : phase 3.
- Stockage des photos sur blob storage, si retenu par la DSI : nouvelle implémentation de `IFileStorage`.

## Phase 3 — Caisse WPF, hors ligne, synchronisation ✅ (validée)

### Livré

**Serveur — synchronisation des caisses** (`/api/v1/register/*`, jeton caisse uniquement)
- Profil de la caisse (mentions fiscales, dernière séquence/empreinte/Z connus du serveur).
- Données de référence **incrémentales** (`rowversion` + `MIN_ACTIVE_ROWVERSION`), cloisonnées : catalogue, opérateurs
  de la société/du site (hachés de PIN pour la connexion hors ligne), contrats, règles de subvention, convives, badges
  et soldes des seuls contrats acceptant ce point de vente, menus publiés d'hier à demain ; instantané complet si un
  contrat change ou toutes les 24 h.
- Consultation de badge en ligne (solde frais, subvention déjà accordée aujourd'hui sur tous les points de vente).
- Réception **idempotente** : sessions, mouvements (débit en ligne avec solde imposé / rejeu hors ligne marqué),
  tickets (ordre de séquence, chaînage, reconstruction et contrôle de l'empreinte, mouvement présent pour chaque
  paiement compte), Z (recalcul serveur et refus en cas d'écart).
- Back-office : **Tickets & avoirs** (filtres, détail, empreintes) et **Clôtures Z & intégrité** (vérification de chaîne).

**Caisse** — `Newrest.Pos.Client.Core` (logique, multiplateforme) + `Newrest.Pos.Client` (WPF) + `Client.Data` (SQLite)
- Enregistrement (URL, identifiant, clé chiffrée DPAPI), reprise de la chaîne après réinstallation.
- Connexion opérateur par PIN **hors ligne** avec verrouillage, ouverture (fond de caisse), vente, paiement, recharge,
  consultation de solde, historique (réimpression « DUPLICATA », avoir responsable), clôture Z (comptage à l'aveugle).
- Vente : menu du jour par catégorie + recherche, badge (en ligne puis cache), subvention, paiements espèces (rendu),
  TPE autonome (référence d'autorisation), compte convive, mixtes ; **3 gestes** pour un plateau standard.
- **Transaction SQLite unique** par ticket (séquence + ticket + outbox) ; outbox ordonnée ; plafond hors ligne par badge.
- Indicateur permanent en ligne / hors ligne / en attente / file bloquée.
- Périphériques (`Newrest.Pos.Devices`) : ESC/POS (TCP 9100, spouleur Windows, fichier/partage), tiroir, lecteur de
  badge clavier (détection de rafale) et série, afficheur client (second écran WPF), TPE manuel, caméra (interface) +
  simulateurs pour chacun.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build Release complet (dont WPF compilé sous Linux), avertissements = erreurs, `dotnet format` | ✅ |
| Modèles EF serveur et caisse (SQLite) ↔ migrations | ✅ |
| Domain 104/104 · Devices 9/9 · Intégration SQL 13/13 · API 28/28 · **Scénarios caisse 7/7** | ✅ 161 tests |
| Scénario hors ligne : coupure, **20 ventes**, plafond atteint (2 débits compte puis 3 refus → espèces), retour réseau avec **réponses perdues** puis normal → 20 tickets côté serveur, séquences 1–20, aucun doublon, chaîne valide, solde exact | ✅ |
| Scénario bout en bout : badge + subvention → ticket imprimé (mentions, subvention, part convive, solde) → synchronisation → visible en back-office | ✅ |
| Avoir + recharge + Z : le Z de la caisse = Z recalculé par le serveur ; session close des deux côtés | ✅ |
| Caisse réinstallée : la séquence et le n° de Z reprennent ; chaîne valide | ✅ |
| Refus serveur définitif : file bloquée et signalée, rien n'est sauté, reprise après intervention | ✅ |
| Couverture lignes | ✅ Domain 90,3 %, Application 89,9 % (seuils 80 %) ; Client.Core 74,9 % (scénarios) |
| Recette réelle : API + back-office lancés, caisse sans interface pilotant l'API (5 ventes, avoir, Z), pages Tickets et Clôtures dans le navigateur | ✅ |
| **Altération directe d'un ticket en base SQL** puis vérification depuis le back-office | ✅ « n° 3 : contenu modifié » détecté |
| Application WPF **exécutée** | ⚠️ non exécutée : environnement Linux sans affichage. Le XAML compile, les ViewModels sont testés ; recette visuelle à faire sur un poste Windows (voir ci-dessous) |
| Workflows GitHub Actions | ⚠️ non exécutés sur GitHub (aucune PR) ; rejoués en local |

Défauts trouvés et corrigés : montants à l'écran dépendant de la culture de la machine, échelle décimale de la
subvention (25,0000), CI des phases 2 et 3 incomplète (étapes API/scénarios absentes à cause d'un remplacement raté).

### Décisions prises
Voir `docs/assumptions.md` A49–A65. Points saillants à valider :
- **Plafond hors ligne : 60 MAD par compte et par caisse** (configurable).
- Hors ligne, le plafond journalier de subvention ne voit que les tickets de la caisse (risque borné à un repas).
- Débit en ligne effectué avant l'émission du ticket ; cas rare de « débit sans ticket » à couvrir par un rapport de
  rapprochement (phase 6).
- Avoir uniquement sur la caisse émettrice, validé par un responsable.

### Reste à faire / points ouverts
- **Recette visuelle WPF sur Windows** (écran tactile, lecteur de badge réel, imprimante ESC/POS réelle, second écran).
- Bouton responsable « relancer la file » et clôture forcée dans l'interface (la logique existe).
- Sauvegarde SQLite en rotation, installeur MSIX, mise à jour automatique : phase 6.
- Rapport de rapprochement débits/tickets : phase 6.

## Phase 4 — Service vision ✅ (validée)

### Livré

**Service vision local** (`vision/`, Python 3.12, FastAPI, dépendances verrouillées par uv)
- `POST /recognize` (image + articles du menu du jour + photos de référence) → `{items: [{article_code, confidence,
  bbox, bbox_yolo, alternatives}], provider, latency_ms, …}` ; `POST /feedback` ; `GET /health` ;
  `GET /camera/capture` (OpenCV, exposition/balance fixes, **recadrage plateau**) ; `POST /dataset/upload`.
- Providers derrière une interface commune, choisis par configuration **relue à chaud** (`vision.json`) :
  `mock` (déterministe, scénarios par image), `gemini` (Google Gen AI : schéma JSON avec `article_code` en énumération
  des candidats, température 0,1, image 1024 px, photos de référence, `box_2d` 0-1000 → pixels → YOLO), `yolo` et
  `hybrid` réservés à la phase 5 (503 explicite).
- Garde-fous : codes hors menu **écartés**, délai par requête (504), erreurs du modèle isolées (502/503), image
  invalide (400), **visage détecté → ni envoyé au modèle ni stocké** (422), écoute 127.0.0.1 uniquement.
- Dataset local : image + prédictions + lignes validées + **labels YOLO** dérivés (boîte gardée si la caissière
  corrige l'article), « à annoter » si une unité n'a pas été détectée, rotation, envoi central optionnel (URL signée).
- Clé Gemini : **fichier chiffré DPAPI (portée machine)** sur le poste ; installation en service Windows (NSSM) par
  `deploy/vision/install-vision-service.ps1`.

**Caisse** (`Client.Core/Vision`, écran de vente WPF)
- Bouton **📷 Photo plateau** (ou F2) : capture via le service, reconnaissance bornée à **6 s** ; tout échec (service
  arrêté, caméra, délai, modèle, visage) affiche un message et laisse la saisie manuelle — la vente n'est jamais bloquée.
- Seuils (configurables, défaut 0,60/0,90) : ≥ 0,90 ajout automatique ; 0,60–0,90 ligne **en surbrillance** avec
  « OK » et **« → 2e choix »** ; < 0,60 bandeau « catégorie : choisir dans le menu », l'article choisi est rattaché à
  la prédiction. Les lignes du ticket portent leur origine (`VisionAuto/Confirmed/Corrected/Manual`).
- Après la vente : retour d'apprentissage au service (labels du dataset) et statistiques au serveur via l'outbox
  (jamais bloquant pour la file fiscale). Photos de référence téléchargées une fois et mises en cache.

**Serveur et back-office**
- `POST /api/v1/register/recognitions` (idempotent), `GET /api/v1/register/photos/{id}`, articles synchronisés avec
  description visuelle et photos (l'ajout/suppression d'une photo déclenche la synchronisation incrémentale).
- Page **Performance vision** + `GET /api/v1/vision/stats` : plateaux, indisponibilités, latence moyenne/p95, taux
  d'ajout automatique et de correction par site et par provider, articles les plus corrigés, confusions fréquentes ;
  cloisonnée par périmètre.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build Release complet (avertissements = erreurs), `dotnet format`, `ruff check` + `ruff format --check` | ✅ |
| Domain 104 · Devices 9 · Intégration SQL 13 · API **31** · Scénarios caisse **20** | ✅ 177 tests .NET |
| Service vision (pytest) : schéma de réponse, **rejet des codes hors menu**, **délai dépassé**, erreurs provider, visages, dataset/labels, envoi central, requête Gemini (faux client : schéma, température, 1024 px, photos), clé DPAPI, caméra, bascule de provider à chaud, écoute locale | ✅ 44 tests |
| Vente assistée (3 seuils, 2e choix, catégorie, retour d'apprentissage, statistiques serveur, origine des lignes du ticket) : **plateau encaissé en moins de 10 s** (assertion du test) | ✅ |
| Service lent (30 s) → repli manuel en < 3 s, vente encaissée ; service arrêté ; modèle en erreur | ✅ |
| **Caisse + vrai service Python** (provider mock, port local) : photo → propositions → encaissement → dataset avec lignes validées et labels YOLO ; caméra absente signalée | ✅ |
| Couverture lignes | ✅ Domain 90,3 %, Application 90,2 % (seuils 80 %) ; Client.Core 77,9 % (scénarios) |
| Back-office dans le navigateur (parcours complet + page Performance vision avec données) | ✅ |
| **Appel réel à Gemini** | ⚠️ non effectué : aucune clé dans cet environnement. Requête et analyse de réponse testées avec un faux client ; premier essai réel à faire avec une clé de test (`VISION_PROVIDER=gemini`, `GET /health`, une photo) |
| Caméra réelle, DPAPI, service Windows (NSSM), WPF | ⚠️ Windows requis : recette sur un poste caisse |

Défauts trouvés et corrigés : OpenCV 5 sans cascades de Haar (version bornée < 5), limite de 1 Mo des champs de
formulaire pour les photos de référence (passées en fichiers), téléchargement concurrent des photos (sérialisé),
variables d'environnement vides interprétées comme des valeurs, image avec visage initialement envoyée au modèle
(désormais refusée avant tout traitement).

### Décisions prises
Voir `docs/assumptions.md` A66–A81. Points saillants à valider :
- **Clé Gemini en coffre DPAPI local** plutôt que relayée par le serveur (latence, pas de point de panne central).
- Caméra lue par le service vision ; recadrage réglé et contrôlé à l'installation de chaque caisse.
- Image avec visage : refusée (saisie manuelle) — faux positifs à mesurer pendant le pilote.
- Payer sans toucher une ligne en surbrillance vaut confirmation.
- Transfert des images de plateau à Google (Gemini) : à déclarer à la CNDP (`docs/privacy.md`).

### Reste à faire / points ouverts
- Essai réel Gemini sur des photos de plateaux Newrest (latence et précision réelles, réglage des seuils).
- Réglage des seuils par site depuis le back-office (aujourd'hui par caisse, `Register:Vision`).
- Phase 5 : entraînement YOLO sur le dataset collecté, providers YOLO et Hybrid, déploiement des modèles depuis le
  serveur, calibration des confiances.

## Phase 5 — Entraînement YOLO, providers YOLO/Hybride, déploiement des modèles ✅ (validée)

### Livré

**Entraînement** (`vision/training`, machine dédiée, `uv sync --extra training`)
- `annotate` : images où une unité n'a pas été détectée, avec pré-étiquettes, pour CVAT / Label Studio.
- `build` : dataset YOLO à partir des enregistrements des caisses (plateaux encaissés, labels issus des corrections),
  annotations intégrées, indices de classes conservés d'un modèle à l'autre, découpage déterministe, rapport.
- `train` : Ultralytics YOLO (yolo11n par défaut), validation (mAP50/mAP50-95 par article), **refus sous mAP50 0,5**,
  export ONNX contrôlé, paquet `model.onnx` + `manifest.json` (classes, métriques, SHA-256).
- `synthetic` : plateaux synthétiques pour démontrer et tester toute la chaîne sans photos réelles.

**Service vision** : provider **`yolo`** (ONNX Runtime, sans PyTorch ni Internet ; classes hors menu masquées) et
**`hybrid`** (YOLO puis Gemini si doute ou nouvel article, fusion par boîte, repli YOLO) ; stockage des modèles
(empreinte et forme vérifiées, 3 versions gardées) ; `PUT /models/{v}`, `PUT /runtime` ; configuration en couches
(environnement < réglages du site < secours local).

**Serveur et back-office** : page **Modèles vision** (import avec contrôle d'empreinte, brouillon → publié → retiré,
réglages par site : moteur, modèle fixe ou « dernier publié », seuils, activation ; **état de chaque caisse** :
attendu / appliqué / erreur) ; **calibration des seuils** sur la page Performance vision (précision par tranche de
confiance, seuils suggérés, filtre par moteur/modèle).

**Caisse** : `VisionDeploymentService` après chaque synchronisation de référence, hors file fiscale : seuils du site
appliqués à l'écran de vente, téléchargement du modèle **contrôlé SHA-256**, installation dans le service, bascule,
compte rendu au serveur ; état vision dans la barre d'état.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build Release (avertissements = erreurs), `dotnet format`, modèle EF ↔ migrations (`AddVisionDeployment`), `ruff`, `uv lock --check` | ✅ |
| Domain 113 · Devices 9 · Intégration SQL 13 · API **33** · Scénarios **25** | ✅ 193 tests .NET |
| Service vision : **65** tests (décodage YOLO, stockage des modèles, `/models` `/runtime`, hybride, dataset d'entraînement…) | ✅ |
| **Entraînement réel** (`pytest -m training`, CPU) : 120 plateaux synthétiques → 40 époques → mAP50 ≥ 0,5 → ONNX → installé dans le service → plateau reconnu correctement ; seuil qualité qui refuse un modèle trop faible | ✅ 2 tests, ~3 min |
| Déploiement : site → téléchargement → empreinte → installation → bascule → seuils appliqués → état remonté ; pas de re-téléchargement ; retour à Gemini et retour arrière ; « dernier publié » vs modèle fixé ; **modèle altéré dans le stockage serveur refusé** (moteur inchangé, erreur visible) ; site désactivé ; service injoignable ; secours local signalé | ✅ |
| **Bout en bout réel** : modèle publié en back-office → API → caisse → **vrai service Python** (ONNX) → photo → lignes ajoutées par YOLO → vente → statistiques « yolo:<version> » → caisse « à jour » | ✅ |
| Back-office dans le navigateur : import d'un **modèle réellement entraîné** (mAP50 99,5 % sur données synthétiques), publication, réglage d'un site en hybride, état des caisses | ✅ |
| Couverture lignes | ✅ Domain 90,4 %, Application 90,2 % ; Client.Core 77,6 % |
| Entraînement sur **vraies photos de plateaux Newrest** | ⚠️ impossible ici (aucune donnée réelle) : la mAP50 de 0,995 porte sur des formes synthétiques et ne préjuge pas de la précision réelle |
| GPU, service Windows, WPF, workflows GitHub (dont `vision-training.yml`) | ⚠️ non exécutés ici (Linux, CPU, pas de GitHub Actions) |

Défauts trouvés et corrigés : le script d'installation créait un `vision.json` qui aurait bloqué les réglages du
back-office ; un site désactivé n'aurait jamais été réactivé (le déploiement dépendait de l'activation) ; seuils
affichés avec 4 décimales ; stockage de fichiers partagé entre tests (rendu propre à chaque instance).

### Décisions prises
Voir `docs/assumptions.md` A82–A95. Points saillants à valider :
- **Licence Ultralytics (AGPL-3.0)** : licence Enterprise nécessaire pour un usage commercial fermé, ou autre
  détecteur au même format ONNX — **décision juridique/achats avant la production**.
- Déploiement **tiré par les caisses** (5 min), jamais poussé ; publication manuelle par un administrateur.
- Le mode hybride est recommandé pour démarrer un site : Gemini ne sert que lorsque YOLO hésite.
- Seuils suggérés (97 % de justes pour l'ajout automatique) : jamais appliqués automatiquement.

### Reste à faire / points ouverts
- Premier entraînement sur les données du site pilote (après quelques semaines en Gemini/hybride), sur GPU.
- Stockage d'entraînement central (conteneur, droits, durée de conservation) à provisionner.
- Phase 6 : durcissement, installeurs, supervision, runbook complet, tests de charge, checklist pilote.

## Phase 6 — Production ✅ (en attente de validation)

### Livré

**Supervision** (back-office *Supervision*, webhook Teams/Slack, métriques OpenTelemetry `pos.*`)
- Battement de cœur des caisses (version, file, blocage, session, sauvegarde), alertes calculées (caisse ouverte muette,
  file bloquée ou en retard, Z non fait, sauvegarde absente, chaîne invalide ou non vérifiée, débit sans ticket,
  découvert hors ligne, vision), notification dédupliquée.
- **Vérification nocturne de toutes les chaînes** (par lots), historique, bouton « vérifier maintenant ».
- **Rapprochement** : débits de compte sans ticket (contre-passation finance désormais possible dans ce seul cas), ventes
  hors ligne au-delà du découvert, tickets reçus en retard.

**Conformité** (back-office *Archives & conformité*)
- **Archives mensuelles signées ECDSA** : tickets recalculables, originaux des avoirs, Z, mouvements, points de contrôle
  de chaque caisse ; continuité d'une archive à l'autre ; refus de sceller une chaîne rompue ; re-vérification serveur et
  **vérification hors ligne** `Newrest.Pos.Migrator --verify-archive`.
- **Anonymisation** des convives partis (simulation puis exécution, journalisée).

**Caisse** : sauvegardes SQLite cohérentes (quotidienne + après chaque Z, 7 gardées), écran **Responsable** (file en
attente, relance après refus, sauvegarde immédiate), **clôture forcée**, version et état vision dans la barre d'état.

**Durcissement et livraison**
- Démarrage **refusé en production** si la configuration est incomplète ou de développement (`sa`, SQL non chiffré,
  secrets absents, clé éphémère, connexion de développement, PIN faibles) — tous les problèmes listés d'un coup.
- En-têtes de sécurité (CSP stricte du back-office, HSTS, X-Frame-Options…), cookies `Secure`, en-têtes de proxy filtrés.
- **Images Docker** non root (API, back-office, migrateur), compose de production + modèle de configuration ;
  **paquet caisse** autonome (zip + `install-register.ps1`, mise à jour conservant données et clé).
- CI : audit des vulnérabilités NuGet et Python, images construites et refus de configuration vérifié, paquet caisse
  publié en artefact, couverture des scénarios ; Dependabot.
- **Outil de charge** `tools/Newrest.Pos.LoadTest` et résultats (`load-test.md`).
- Documentation : `runbook.md` complet (déploiement, alertes, sauvegardes/restauration, archives, rotation des secrets),
  `pilot-checklist.md`, `security.md`, `deploy/sql/maintenance.sql`.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build Release (avertissements = erreurs), `dotnet format`, modèles EF ↔ migrations, `ruff`, audit NuGet + `pip-audit` | ✅ aucune vulnérabilité connue |
| Domain 119 · Devices 9 · Intégration 16 · API 33 · Scénarios **30** · vision 65 (+2 entraînement) | ✅ 207 tests .NET |
| **Falsification SQL d'un ticket → alerte critique → webhook** (une seule fois) ; débit sans ticket → rapprochement → contre-passation ; file bloquée ; périmètres | ✅ |
| **Archives** : création, vérification serveur et par le Migrator (code 0), falsification détectée (empreinte de fichier, signature, contenu recalculé), ticket arrivé après l'archivage repris dans l'archive suivante avec continuité de chaîne | ✅ |
| Images Docker construites ; **conteneur API refusant une configuration de développement** en production ; migrateur conteneurisé sur base neuve ; API conteneurisée saine avec en-têtes de sécurité | ✅ |
| Paquet caisse Windows construit sous Linux (exécutable autonome + service vision + scripts, empreinte SHA-256) | ✅ |
| **Charge** (API 2 vCPU/1 Go) : 60 caisses à 6× la pointe réelle → p95 < 60 ms ; 100 caisses sans pause → ~10 000 plateaux/min, **0 erreur** ; 7 838 tickets vérifiés intègres en 0,9 s | ✅ |
| Back-office dans le navigateur avec CSP : 30/30 étapes, aucune violation CSP | ✅ |
| Couverture | ✅ Domain 89,4 % ; Application 94,3 % (API + scénarios) ; Client.Core 77,8 % |
| Scripts PowerShell (installation caisse et service vision), WPF, NSSM, DPAPI, Intune | ⚠️ Windows requis : non exécutés ici |
| Workflows GitHub (CI, entraînement, Dependabot) | ⚠️ non exécutés sur GitHub ; étapes rejouées en local |
| Charge sur l'infrastructure cible, restauration SQL réelle, test d'intrusion | ⚠️ à faire (checklist pilote) |

Défauts trouvés et corrigés : **limite de demandes de jeton (20/min/IP) qui aurait bloqué un site entier derrière une même
adresse publique** (trouvé par le test de charge) ; sauvegardes de la même seconde qui s'écrasaient ; script de recette
du back-office dépendant du jour de la semaine ; une consommation sans ticket ne pouvait pas être annulée par la finance.

### Décisions prises
Voir `docs/assumptions.md` A96–A109. Points saillants à valider :
- Archives : une par société et par mois, à partir du 3 du mois suivant, format JSON + manifeste signé — **à faire
  valider par l'expert-comptable / la DGI** (avec la durée et le support de conservation).
- Contre-passation d'un débit sans ticket par la finance (exception à la règle « une consommation s'annule par avoir »).
- Livraison caisse en zip + PowerShell (Intune/SCCM) plutôt que MSIX.
- Seuils d'alerte par défaut (30 min, 50 éléments, 20 h, 30 h, 36 h, 2 h).

### Reste à faire avant la généralisation
- Dérouler `pilot-checklist.md` (décisions fiscales et CNDP, infrastructure, recette sur site).
- Recette Windows réelle (WPF, périphériques, installation, Intune) et test d'intrusion.
- Charge et restauration sur l'infrastructure cible ; provisionner le stockage WORM des archives.
