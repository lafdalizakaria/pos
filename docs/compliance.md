# Conformité fiscale et comptable — points à faire valider

> Ce document **ne constitue pas un avis fiscal**. Il liste ce que la solution implémente et ce que
> l'expert-comptable de Newrest Maroc et, si besoin, la DGI doivent confirmer avant la mise en production du site pilote.

## 1. Ce que la solution garantit techniquement

| Exigence | Implémentation | Statut |
|---|---|---|
| Numérotation continue sans trou par caisse | Séquence générée par la caisse, unique en base (`RegisterId`, `Sequence`), détection des trous (`TicketNumber.FindGaps`, `TicketChainVerifier`) | Phase 1 ✔ |
| Inaltérabilité des tickets | Entités immuables (aucun setter public), intercepteur EF qui refuse toute modification/suppression, `DENY UPDATE, DELETE` SQL pour le compte applicatif (`deploy/sql/least-privilege.sql`) | Phase 1 ✔ |
| Correction uniquement par avoir | `Ticket.IssueCreditNote` (avoir lié, motif obligatoire, même séquence) | Phase 1 ✔ (avoir total) |
| Intégrité / traçabilité | Chaîne SHA-256 : chaque ticket contient le hash du précédent de la même caisse ; vérification (altération, suppression, insertion, doublon) ; le serveur refuse tout ticket hors séquence ou dont l'empreinte ne correspond pas | Phase 3 ✔ (écran « Clôtures Z & intégrité ») ; altération SQL directe détectée lors de la recette |
| Clôture journalière (Z) | Z numéroté par caisse, totaux par moyen de paiement, par taux de TVA, subventions, recharges, écart de caisse, hash du dernier ticket ; recalculé et contrôlé par le serveur | Phase 3 ✔ (caisse, impression, serveur) |
| Journal des actions sensibles | `AuditLog` immuable | Modèle phase 1 ; alimentation phase 2 |

## 1 bis. Mentions imprimées aujourd'hui sur le ticket

Raison sociale, site et adresse, ICE, IF, RC, « TICKET » ou « AVOIR » + numéro (et motif), date et heure locales,
caisse, caissier, convive (si badge), lignes (quantité, libellé, montant), base HT et TVA par taux, total TTC,
subvention employeur et part convive, paiements (espèces reçues et rendu, carte avec autorisation, compte convive),
solde du compte, 16 premiers caractères de l'empreinte, mention « DUPLICATA » sur les réimpressions.
Les recharges impriment un « REÇU DE RECHARGE (n'est pas une facture) ».

## 2. Questions ouvertes pour l'expert-comptable / la DGI

1. **Mentions obligatoires du ticket** : raison sociale, ICE, IF, RC, adresse du site, numéro et date/heure, détail des
   articles, base HT et TVA par taux, total TTC, moyen de paiement, part subvention / part convive. Faut-il mentionner
   le numéro de patente ou d'autres identifiants ? Format de l'ICE sur le ticket ?
2. **Taux de TVA applicables** à la restauration collective (plats, boissons servies, boissons conditionnées, pain,
   prestations sous contrat d'entreprise). Les taux de démonstration (10 % / 20 % / 0 %) sont indicatifs.
3. **Subvention employeur** : traitement en TVA de la part payée par l'employeur (même base que la vente ? facture
   mensuelle distincte ?). La solution calcule la TVA sur le TTC total du ticket, indépendamment de qui paie.
4. **Recharges de compte** : confirmer qu'une recharge prépayée est un encaissement d'avance (pas de chiffre d'affaires,
   TVA exigible à la consommation). Le comportement est implémenté ainsi et sera rendu configurable (phase 3).
5. **Postpayé / retenue sur salaire** : forme de la facture mensuelle à l'employeur, rapprochement avec les tickets.
6. **Avoirs** : un avoir partiel est-il nécessaire ? Délai maximal pour émettre un avoir ? Un avoir sur une autre
   caisse que le ticket d'origine est-il admis ? (La solution numérote l'avoir dans la séquence de la caisse émettrice.)
7. **Durée de conservation et archivage** : 10 ans pour les documents comptables (Code de commerce, art. 22, et Code
   général des impôts) — confirmer la durée, le support (électronique accepté ?) et le format d'export attendu en cas
   de contrôle. La chaîne de hash doit être conservée avec les tickets ; prévoir des points de contrôle signés lors
   des archivages.
8. **Certification de logiciel de caisse** : existe-t-il, à la date du projet, une obligation de certification ou de
   déclaration du logiciel de caisse au Maroc ? (À vérifier dans la dernière loi de finances.)
9. **Ticket papier vs électronique** : l'impression est-elle obligatoire pour chaque vente (y compris 100 % subventionnée) ?
10. **Écarts de caisse** : traitement comptable et seuil de justification.

## 3. Archivage (à détailler en phase 6)

- Sauvegardes SQL Server complètes + journaux, rétention conforme au point 7.
- Export périodique signé (tickets + chaîne + Z) vers un stockage WORM / immuable.
- La vérification de la chaîne peut reprendre à un point de contrôle (`TicketChainVerifier.Verify(..., firstExpectedSequence, expectedPreviousHash)`).
