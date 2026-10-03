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
