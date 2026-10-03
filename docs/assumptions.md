# Hypothèses et valeurs par défaut

Chaque hypothèse ci-dessous a été prise pour ne pas bloquer le développement. Elles sont **configurables** sauf mention
contraire et doivent être confirmées par le métier (M), la finance / l'expert-comptable (F) ou la DSI (D).

| # | Sujet | Hypothèse retenue | Où | À valider |
|---|---|---|---|---|
| A1 | Version .NET | .NET 10 (LTS, nov. 2025 → nov. 2028), C# latest | `global.json` | D |
| A2 | Identifiants | GUID v7 (ordonnés dans le temps) générés par le domaine, y compris hors ligne par les caisses ; jamais par la base | `Entity` | D |
| A3 | Montants | `decimal(18,2)` en MAD, arrondi commercial (demi au-dessus, *away from zero*) | `Money` | F |
| A4 | Prix | Tous les prix sont **TTC**. TVA ligne = `TTC × taux / (1 + taux)` arrondie au centime ; TVA ticket = somme des TVA lignes ; TVA Z = somme des TVA lignes par taux | `Money`, `TicketLine` | F |
| A5 | Taux de TVA démo | 10 % plats et boissons servies, 20 % boissons conditionnées (eau, sodas), 0 % pain. **Valeurs indicatives uniquement** | `DemoDataSeeder` | F |
| A6 | Numérotation | `{Préfixe caisse}-{séquence sur 8 chiffres}`, ex. `CAS1-00000042`. Séquence unique par caisse, **partagée par ventes et avoirs**, commence à 1. Préfixe unique (2–12 caractères A–Z/0–9), immuable | `TicketNumber`, `Register` | F |
| A7 | Avoirs | Phase 1 : avoir **total** uniquement (un avoir au plus par ticket, contrainte unique). Avoir partiel à étudier | `Ticket.IssueCreditNote` | M/F |
| A8 | Remboursement d'un avoir | Par défaut, mêmes moyens de paiement que le ticket d'origine, montants négatifs ; personnalisable | `CreditNoteRequest.RefundPayments` | M |
| A9 | Chaînage | SHA-256 hexadécimal minuscule du JSON canonique du ticket (ordre fixe, montants `0.00`, taux `0.0000`, horodatage UTC à la milliseconde) + hash précédent. Premier ticket chaîné sur 64 zéros. Format versionné (`v1`) | `TicketHasher` | F |
| A10 | Horodatage ticket | Tronqué à la milliseconde, stocké en UTC (stabilité du hash sur SQL Server et SQLite) | `Ticket` | — |
| A11 | Subvention : base | La subvention s'applique au montant des articles **subventionnables** (`Article.IsSubsidizable`) ; boissons conditionnées non subventionnables en démo | `SubsidyCalculator` | M |
| A12 | Subvention : ordre des plafonds | 1) % ou forfait (forfait ≤ montant éligible) → 2) plafond par repas → 3) plafond journalier restant (tous points de vente confondus, net des avoirs, jamais négatif) → 4) nombre max. de repas subventionnés/jour | `SubsidyCalculator` | M |
| A13 | Subvention : sélection de règle | Règle valide à la date ; une règle ciblant la catégorie du convive (ex. `CADRE`) prime sur une règle générique ; à égalité, la plus récente | `SubsidyCalculator.SelectRule` | M |
| A14 | Subvention : comptabilisation | La subvention est portée par le ticket (`SubsidyAmount`) et facturée à l'employeur ; seul le **reste à charge** du convive est débité du compte (`Consumption`). Le type de mouvement `Subsidy` sert aux dotations employeur créditées sur le compte | `Ticket`, `Account` | F |
| A15 | Types de compte | Prépayé (découvert 0 par défaut), Postpayé (`OverdraftLimit` = plafond de crédit), Mixte (solde positif d'abord puis découvert jusqu'au plafond ; le solde négatif est facturé en fin de mois) | `Account` | M/F |
| A16 | Signe des mouvements | `TopUp`, `Subsidy` > 0 ; `Consumption` < 0 ; `Refund` (± : + = remboursement d'avoir sur le compte, − = solde rendu au convive), `Correction` et `Transfer` : tout montant non nul | `Ledger` | F |
| A17 | Contre-passation | Une seule annulation par mouvement (index unique) ; une contre-passation ne peut pas être annulée ; elle ne peut pas rendre le solde inférieur au découvert autorisé | `Account.Reverse` | M |
| A18 | Ventes hors ligne au-delà du solde | Une vente déjà réalisée hors ligne et synchronisée est **acceptée** même si elle dépasse le découvert (la vente a eu lieu), mais marquée `ExceededOverdraft` et journalisée pour suivi. Le risque est borné par le plafond hors ligne par badge et par caisse (phase 3). Toute opération **en ligne** reste strictement refusée au-delà du découvert | `Account.Post` | M/F |
| A19 | Points de vente acceptés | Définis au niveau du **contrat** (et non du compte) | `Contract` | M |
| A20 | Badges | Le numéro de badge est unique globalement et n'est jamais réutilisé ; le solde appartient au compte, jamais au badge | `Badge` | M |
| A21 | Un compte par convive et par contrat | Index unique (`DinerId`, `ContractId`) | EF | M |
| A22 | Opérateurs | Rattachés à une société, éventuellement restreints à un site. PIN 4 à 8 chiffres, PBKDF2-SHA256 600 000 itérations, verrouillage 15 min après 5 échecs | `Operator`, `Pbkdf2PinHasher` | D |
| A23 | Seuils vision | Haut 0,90 / bas 0,60 (bornes incluses : 0,90 → ajout auto ; 0,60 → à confirmer) | `RecognitionConfidencePolicy` | M |
| A24 | Fuseau horaire | `Africa/Casablanca` par défaut, par site ; la date d'exploitation (`BusinessDate`) est la date de la session de caisse | `Site`, `CashSession` | M |
| A25 | Une session ouverte par caisse | Index unique filtré `Status = 'Open'` | EF | M |
| A26 | Recharges dans le Z | Les recharges encaissées à la caisse (mouvements portant `RegisterId` + moyen de paiement) figurent dans une section distincte et dans le fond de caisse attendu, **jamais dans le CA ni la TVA** (comportement « recharge = mouvement ») | `ZReportCalculator` | F |
| A27 | Énumérations | Stockées en texte (lisibles par la DSI et les outils BI) ; rôles opérateurs (flags) en entier | `PosDbContext` | D |
| A28 | Suppressions | Aucune suppression en cascade sur les données métier, sauf enfants d'agrégats modifiables (photos, surcharges de prix, lignes de menu, points de vente d'un contrat) | `PosDbContext` | D |
| A29 | FluentAssertions | Version 7.x (licence Apache 2.0). La v8+ exige une licence commerciale Xceed pour un usage en entreprise | `Directory.Packages.props` | D |
| A30 | Données de démonstration | Sociétés NFMS et NMS, clients **fictifs** (Atlas Automotive, Sahara Aero), PIN démo `1234` (caissier) / `5678` (responsable) ; refusées en Production | `DemoDataSeeder`, Migrator | — |

## Phase 2 — API centrale et back-office

| # | Sujet | Hypothèse retenue | Où | À valider |
|---|---|---|---|---|
| A31 | Rôles back-office | Rôles d'application Entra ID : `Pos.Admin` (global), `Pos.Manager` (gestion dans son périmètre), `Pos.Accountant` (finance : corrections, contre-passations, remboursements, audit), `Pos.Viewer` (lecture) | `PosRoles` | M/D |
| A32 | Périmètres | Rôles portés par Entra ID, **périmètres** (société entière ou site) gérés dans le back-office par les administrateurs (`UserAccessScopes`, par UPN). Un administrateur est global | `AccessControl` | D |
| A33 | Cloisonnement des clients | Clients, contrats, convives et comptes sont cloisonnés **par société** : un responsable de site voit les clients de sa société ; la gestion des clients/contrats/règles exige un périmètre société entière | `AccessControl` | M |
| A34 | Catalogue maître | Articles et catégories : administrateurs uniquement. Les responsables agissent via les listes de prix de leur périmètre | `CatalogService` | M |
| A35 | Code article immuable | Le code est imprimé sur les tickets et sert aux modèles de vision : il ne change jamais (créer un nouvel article) | `CatalogService` | M |
| A36 | Préfixe caisse immuable | Le préfixe et le point de vente d'une caisse ne changent pas après création (numérotation fiscale) | `OrganizationService` | F |
| A37 | Clé de caisse | Clé aléatoire de 256 bits (`nrpos_…`) affichée **une seule fois**, seul son SHA-256 est stocké ; renouvellement = l'ancienne clé cesse de fonctionner ; désactiver une caisse révoque sa clé | `DeviceKeys` | D |
| A38 | Jeton caisse | JWT HS256 de 15 min (configurable 1–60) émis par l'API contre la clé ; endpoint limité à 20 requêtes/min/IP. Le certificat client (mTLS) reste possible en phase 6 | `RegisterTokenIssuer` | D |
| A39 | Prix des menus | Le prix d'un article est **résolu à l'ajout au menu** (listes de prix) puis figé ; il reste modifiable dans le menu (audité) | `MenuService` | M |
| A40 | Copie de menus | La copie crée des **brouillons** ; un menu existant est ignoré sauf option « écraser », et un menu publié n'est jamais écrasé | `MenuService` | M |
| A41 | Règles de subvention | Jamais modifiées : clôture (date de fin) + nouvelle règle, car les tickets passés y font référence | `SubsidyRule.Close` | M/F |
| A42 | Recharge | Autorisée aux responsables et à la finance (espèces, carte, virement) ; corrections, contre-passations et remboursements réservés à la finance, motif obligatoire | `AccountService` | F |
| A43 | Annulation d'une consommation | Interdite par contre-passation : une consommation s'annule par un avoir sur le ticket (phase 3) | `AccountService` | F |
| A44 | Remboursement au convive | Limité au solde positif du compte | `AccountService` | F |
| A45 | Import des convives | CSV UTF-8 (`;` ou `,`) ou Excel (.xlsx, 1re feuille), en-têtes FR ou EN, 20 000 lignes max ; mise à jour par matricule ; cellule vide = champ inchangé ; une ligne en erreur est ignorée **sans modification partielle** ; mode simulation disponible ; un badge actif différent n'est jamais remplacé par l'import (utiliser « badge perdu ») | `DinerService` | M |
| A46 | Photos de référence | JPEG/PNG/WebP, 5 Mo max, stockage disque local (`Storage:RootPath`) derrière `IFileStorage` (stockage blob possible sans changement de code) | `LocalFileStorage` | D |
| A47 | Connexion de développement | Formulaire de connexion local (choix utilisateur/rôles) en Development uniquement ; refusé au démarrage en Production | Back-office | D |
| A48 | Audit | Création/modification d'objets de gestion, prix, PIN (sans la valeur), clés de caisse, badges, mouvements manuels, imports, droits : acteur, date, avant/après, IP, corrélation ; écrit dans la même transaction que la modification | `AuditTrail` | F |

## Phase 3 — Caisse, synchronisation, hors ligne

| # | Sujet | Hypothèse retenue | Où | À valider |
|---|---|---|---|---|
| A49 | Architecture caisse | Toute la logique de la caisse (services, hors ligne, synchronisation, ViewModels MVVM) est dans `Newrest.Pos.Client.Core` (multiplateforme, testé), le projet WPF n'est qu'une couche XAML | `Client.Core` | D |
| A50 | Ticket côté caisse | La caisse construit le ticket avec le **même code Domain** que le serveur ; le serveur le reconstruit et refuse toute différence d'empreinte | `TicketSyncMapping` | — |
| A51 | Synchronisation montante | Une file (outbox) SQLite ordonnée, un élément par requête, idempotent côté serveur (identifiant généré par la caisse). Un refus métier **bloque** la file (ordre fiscal) jusqu'à intervention d'un responsable ; les refus « en attente d'un élément précédent » sont réessayés automatiquement | `SyncService` | D |
| A52 | Synchronisation descendante | Incrémentale par `rowversion` (borne `MIN_ACTIVE_ROWVERSION`) ; instantané complet au premier appel, toutes les 24 h ou si un contrat change ; les menus publiés d'hier à demain sont toujours envoyés en entier | `RegisterReferenceService` | D |
| A53 | Débit compte en ligne | Débit immédiat sur le ledger serveur (solde + découvert imposés) **avant** l'émission du ticket ; refus « solde insuffisant » = autre moyen de paiement | `SaleService` | M |
| A54 | Débit compte hors ligne | Autorisé si solde en cache − opérations locales non synchronisées ≥ montant **et** total des débits hors ligne non synchronisés du compte sur cette caisse + montant ≤ **60 MAD** (configurable `OfflineSpendingLimitPerBadge`) | `SaleService` | M/F |
| A55 | Subvention hors ligne | Le plafond journalier hors ligne ne tient compte que des tickets **de cette caisse** (les autres points de vente ne sont pas joignables) ; le risque est borné au plafond journalier d'un repas | `SaleService` | M/F |
| A56 | Débit sans ticket | Cas rare : réponse perdue lors d'un débit en ligne **et** contrôle hors ligne refusé → un débit peut exister côté serveur sans ticket. À traiter par un rapport de rapprochement (phase 6) et une correction finance | — | F |
| A57 | Avoir en caisse | Avoir total d'un ticket **de la même caisse**, validé par un responsable (rôle Supervisor/Admin), motif obligatoire, remboursement par les mêmes moyens (compte recrédité sur le ledger) | `AccountOperationsService` | M/F |
| A58 | Recharge en caisse | Espèces ou carte ; en ligne immédiatement, sinon mise en file ; apparaît dans le Z (section « recharges, hors CA ») et dans les espèces attendues | `AccountOperationsService` | F |
| A59 | Clôture Z | Calculée par la caisse depuis ses tickets ; le serveur la recalcule avec les tickets reçus et **refuse** toute différence (CA net, TVA, espèces attendues, dernier ticket). Comptage « à l'aveugle » puis affichage de l'écart | `CashSessionService`, `RegisterSyncService` | F |
| A60 | Réinstallation d'une caisse | Une caisse sans ticket local reprend la séquence, l'empreinte et le numéro de Z connus du serveur | `RegisterSetupService` | F |
| A61 | Choix du menu | Petit-déjeuner avant 10 h 30, midi avant 16 h, soir ensuite ; « journée » toujours éligible | `SaleViewModel` | M |
| A62 | Lecteur de badge clavier | Rafale de caractères à moins de 60 ms d'intervalle terminée par Entrée, 4 à 64 caractères | `KeyboardWedgeDetector` | D |
| A63 | Impression | ESC/POS, table WPC1252 (accents), 42 colonnes (80 mm). Une panne d'imprimante n'annule jamais la vente : le ticket est enregistré et réimprimable (« DUPLICATA ») | `EscPosEncoder` | D |
| A64 | Clé d'appareil | Chiffrée par DPAPI (portée machine) sur le poste ; jeton caisse de 15 min renouvelé automatiquement | `DpapiDeviceKeyStore` | D |
| A65 | Hachés de PIN en caisse | Les hachés PBKDF2 des opérateurs de la société/du site sont copiés dans la base SQLite de la caisse (connexion hors ligne) ; protéger le poste (BitLocker, compte Windows dédié) | `ReferenceSyncService` | D |
