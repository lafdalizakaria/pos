# Protection des données personnelles (loi 09-08, CNDP)

> Document de travail à compléter avec le DPO / juriste de Newrest Maroc avant toute mise en production.

## Traitement

- **Finalité** : encaissement des repas des salariés des clients B2B, gestion des comptes prépayés/postpayés, calcul et
  facturation des subventions employeur.
- **Responsable de traitement** : la société Newrest titulaire du contrat (NFMS, NMS…). Le client B2B fournit la liste
  des convives : un accord de traitement (sous-traitance / co-responsabilité) doit être signé.
- **Formalités CNDP** : déclaration (ou demande d'autorisation) du traitement « gestion de la restauration collective »
  avant la mise en service ; mise à jour en cas d'ajout de la reconnaissance d'images.

## Données minimales collectées

| Donnée | Usage | Remarque |
|---|---|---|
| Nom, prénom | Affichage caisse, facturation | — |
| Matricule | Rapprochement avec la paie / l'employeur | Unique par client |
| Numéro de badge | Identification à la caisse | Pas de donnée biométrique |
| Catégorie (optionnelle) | Sélection de la règle de subvention | Ex. `CADRE` |
| Client employeur, compte, mouvements, tickets | Facturation, solde | Conservation : voir `compliance.md` |

Aucune donnée sensible (santé, religion, etc.). Les choix de plats ne sont pas utilisés à des fins de profilage.

## Images de plateaux (vision) — phase 4

- **Recadrage strict à la capture** : la caméra est lue par le service vision du poste, qui ne garde que la zone du
  plateau (`VISION_CAMERA_CROP`, réglée et contrôlée visuellement à l'installation de chaque caméra).
- **Détecteur de visages avant tout traitement** : une image où un visage est détecté n'est ni envoyée au modèle ni
  enregistrée ; la caissière saisit le plateau à la main et un message demande de faire recadrer la caméra.
- Les images du dataset (sur le poste, rotation à 20 000) ne sont associées qu'à l'identifiant de reconnaissance, à la
  caisse, au ticket et aux codes articles — **jamais** au convive (le badge n'est pas enregistré dans le dataset).
  Elles ne remontent pas au serveur central ; l'envoi vers un stockage d'entraînement est optionnel (URL signée en
  écriture seule) et à déclarer.
- **Provider Gemini** : l'image recadrée du plateau et la liste des articles du jour sont transmises à Google
  (Gemini API) à chaque reconnaissance → transfert hors du Maroc à mentionner dans la déclaration CNDP et à encadrer
  contractuellement (conditions de traitement des données de l'offre payante, sans utilisation pour l'entraînement
  de Google ; vérifier la durée de rétention applicable). Le provider YOLO local (phase 5) supprime tout transfert.
- **Provider YOLO (phase 5)** : reconnaissance entièrement locale, aucune image ne quitte le poste. En mode hybride,
  seules les images où YOLO hésite (ou contenant un nouvel article) partent chez Google : le volume transféré baisse
  à mesure que le modèle progresse.
- **Entraînement** : les images envoyées vers le stockage d'entraînement (optionnel) sont des plateaux recadrés, sans
  visage (refus en amont), sans lien avec le convive ; accès restreint à l'équipe d'entraînement ; durée de
  conservation à fixer (proposition : 24 mois glissants).
- Clé Gemini chiffrée par DPAPI sur le poste (jamais en clair dans un fichier ou la configuration).
- Statistiques remontées au serveur : codes articles proposés/validés, confiances, latences — aucune image.

## Droits et sécurité

- Accès / rectification / opposition : via le gestionnaire du site ; export des mouvements d'un convive depuis le back-office (phase 2).
- Accès au back-office cloisonné par société et par site (phase 2), journalisation des actions sensibles (`AuditLog`).
- Chiffrement en transit (TLS) partout ; données au repos chiffrées (TDE SQL Server, BitLocker sur les postes caisse).
- Durées de conservation : données comptables selon `compliance.md` ; convive inactif anonymisé après la fin du contrat
  + délai de facturation (proposition : 12 mois), à valider.
