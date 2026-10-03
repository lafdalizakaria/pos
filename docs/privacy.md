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

## Images de plateaux (vision)

- Recadrage strict sur la zone plateau **à la capture** (zone configurable par caisse) : aucune image de visage ne doit
  être enregistrée ; un contrôle visuel est prévu lors de l'installation de chaque caméra.
- Les images ne sont pas associées au nom du convive dans le dataset d'entraînement (seulement au ticket et aux codes
  articles).
- Si le provider Gemini est utilisé, les images sont transmises à Google : à mentionner dans la déclaration CNDP
  (transfert hors du Maroc), avec configuration sans rétention lorsque l'offre le permet. Le provider YOLO local évite
  tout transfert.

## Droits et sécurité

- Accès / rectification / opposition : via le gestionnaire du site ; export des mouvements d'un convive depuis le back-office (phase 2).
- Accès au back-office cloisonné par société et par site (phase 2), journalisation des actions sensibles (`AuditLog`).
- Chiffrement en transit (TLS) partout ; données au repos chiffrées (TDE SQL Server, BitLocker sur les postes caisse).
- Durées de conservation : données comptables selon `compliance.md` ; convive inactif anonymisé après la fin du contrat
  + délai de facturation (proposition : 12 mois), à valider.
