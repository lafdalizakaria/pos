# Checklist du site pilote

Cocher chaque ligne avec la date et le nom. Un point bloquant non coché = pas de démarrage.

## A. Décisions et conformité (avant tout déploiement) — bloquant
- [ ] Questions fiscales tranchées par l'expert-comptable / la DGI (`compliance.md` §2 : mentions du ticket, TVA, subventions, avoirs, conservation).
- [ ] Déclaration CNDP déposée (traitement « restauration collective » + images de plateaux, transfert vers Google si Gemini) — `privacy.md`.
- [ ] Accord de traitement signé avec le client B2B du site (liste des convives).
- [ ] Licence Ultralytics décidée (Enterprise ou autre détecteur) si le mode YOLO/hybride est prévu (A82).
- [ ] Hypothèses métier validées : plafond hors ligne 60 MAD, seuils vision, avoirs, recharges (`assumptions.md`).

## B. Infrastructure centrale — bloquant
- [ ] SQL Server de production (HA), comptes `pos_migrator` / `pos_api` (`deploy/sql/least-privilege.sql`), TDE.
- [ ] Sauvegardes planifiées et **une restauration testée** (`deploy/sql/maintenance.sql`, runbook « Restaurer la base »).
- [ ] Coffre de secrets : clé de signature des caisses, clé ECDSA des archives (clé publique conservée à part), secret Entra, webhook.
- [ ] Entra ID : application, rôles, utilisateurs et **périmètres** saisis dans *Droits d'accès*.
- [ ] Images déployées (API ×2 derrière le reverse proxy TLS, `Supervision:JobsEnabled` sur une seule instance, back-office), migrateur exécuté.
- [ ] Démarrage en `Production` sans avertissement bloquant (journal) ; `/health/ready` = 200 sur les deux services.
- [ ] Collecteur OpenTelemetry + tableau de bord (métriques `pos.*`, latences) ; webhook d'alertes testé (canal d'astreinte).
- [ ] Stockage immuable (WORM) prêt pour les archives mensuelles.

## C. Données du site
- [ ] Société (ICE, IF, RC, adresse), site, points de vente, caisses (**préfixes définitifs**), opérateurs et PIN remis en main propre.
- [ ] Catalogue, catégories, tarifs, menus de la première semaine publiés ; photos de référence (2 par plat, vues du dessus).
- [ ] Client(s), contrat(s), règles de subvention vérifiées sur 3 cas réels avec la finance.
- [ ] Import des convives (simulation puis import), badges testés (un badge par profil de subvention).
- [ ] Comptes prépayés : soldes initiaux saisis et contrôlés (total = état du client).

## D. Postes caisses (par caisse)
- [ ] Windows 10/11 à jour, BitLocker, compte Windows dédié non administrateur, session automatique.
- [ ] Paquet installé (`install-register.ps1`), version visible en *Supervision*.
- [ ] Enregistrement de la caisse (identifiant + clé d'appareil), première synchronisation complète.
- [ ] Imprimante : ticket de test (accents, coupe, tiroir) ; afficheur client sur le second écran ; lecteur de badge.
- [ ] Caméra : **recadrage sur le plateau validé avec une personne devant la caisse (aucun visage)** ; exposition fixe.
- [ ] Service vision : `GET /health` prêt ; réglage du site (Gemini ou hybride) appliqué (✔ dans *Modèles vision*).
- [ ] Sauvegarde locale présente (`C:\ProgramData\Newrest\POS\backups`), heure correcte (NTP).

## E. Recette sur site (avant ouverture)
- [ ] Vente badge + subvention + paiement compte ; vente espèces avec rendu ; carte (référence TPE) ; paiement mixte.
- [ ] Vente assistée par photo : plateau standard encaissé en **moins de 10 s** ; ligne en surbrillance → 2e choix ; catégorie seule.
- [ ] Coupure réseau (câble débranché) : ventes hors ligne, plafond hors ligne atteint, retour réseau, file vidée, aucun doublon.
- [ ] Service vision arrêté : vente manuelle sans blocage.
- [ ] Avoir (responsable), réimpression « DUPLICATA », recharge en caisse.
- [ ] Clôture Z : écart de caisse, Z imprimé ; Z identique côté serveur ; *Clôtures Z & intégrité* : chaîne vérifiée ✔.
- [ ] *Supervision* : aucune alerte en fonctionnement normal ; alerte reçue sur le webhook en éteignant une caisse ouverte 30 min.

## F. Exploitation pendant le pilote (4 à 6 semaines)
- [ ] Astreinte nommée (back-office *Supervision*, webhook), procédure d'escalade (runbook « Incidents »).
- [ ] Revue hebdomadaire : alertes, rapprochement, écarts de caisse, *Performance vision* (seuils suggérés), retours des caissières.
- [ ] Fin du 1er mois + 3 jours : archive mensuelle créée, vérifiée, copiée sur le stockage immuable.
- [ ] Critères de généralisation : 0 ticket perdu, chaîne intègre, Z conformes, ≥ 95 % des plateaux encaissés en < 10 s,
      taux de correction vision stable, aucune alerte critique non expliquée.
