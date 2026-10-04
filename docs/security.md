# Sécurité (phase 6)

Synthèse des mesures, par couche. Revue de sécurité indépendante (test d'intrusion) recommandée avant la généralisation.

## Identités et accès
| Appelant | Mécanisme | Points de contrôle |
|---|---|---|
| Utilisateurs du back-office | Entra ID (OIDC code + PKCE), rôles d'application, périmètres société/site en base | Cookie `HttpOnly`, `Secure` hors développement, `SameSite=Lax`, 8 h glissantes ; connexion de développement **refusée au démarrage** en production |
| Appels API par des personnes | Jeton Entra ID (audience `api://newrest-pos`) | Clé de développement HS256 **refusée en production** |
| Caisses | Clé d'appareil 256 bits (SHA-256 seul stocké, révocable) → jeton HS256 15 min | Clé chiffrée DPAPI sur le poste ; limite 300 demandes/min/IP (configurable) |
| Service vision | Écoute 127.0.0.1 uniquement | Refus de démarrer sur une autre interface ; clé Gemini chiffrée DPAPI |
| Autorisation | Rôle + périmètre vérifiés dans chaque cas d'usage (jamais seulement à l'écran) | Testé par des tests d'API dédiés (périmètres, rôles) |

## Données
- TLS partout (reverse proxy), HSTS hors développement ; SQL Server `Encrypt=True` et certificat vérifié **imposés** en production.
- Compte SQL `pos_api` sans DDL, `DENY UPDATE, DELETE` sur les données fiscales (`deploy/sql/least-privilege.sql`) ; `sa` refusé.
- Tickets, mouvements, Z, audit **immuables** (intercepteur + droits SQL) ; chaîne SHA-256 vérifiée chaque nuit ; archives
  mensuelles **signées ECDSA P-256**, vérifiables hors ligne.
- Secrets : uniquement configuration/coffre (clé de signature caisses, clé d'archives, secret Entra, URL webhook, clé Gemini).
  Le démarrage en production échoue si l'un manque (`ProductionReadiness`).
- Données personnelles : minimisation, anonymisation des convives partis, aucune image de visage (refus en amont), images
  jamais envoyées au serveur central (voir `privacy.md`).

## Application web
- En-têtes : `Content-Security-Policy` (back-office : scripts de la même origine uniquement, pas d'`eval` ; API : `default-src 'none'`),
  `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy` (caméra/micro refusés).
- Anti-falsification Blazor, erreurs sans détail technique (ProblemDetails avec code), journalisation sans secret ni PIN.
- En-têtes `X-Forwarded-*` acceptés seulement depuis les réseaux du reverse proxy (`ForwardedHeaders:KnownNetworks`).
- Téléversements : types et tailles bornés (photos 5 Mo, modèles 300 Mo avec empreinte vérifiée).

## Chaîne logicielle
- CI : avertissements = erreurs, analyseurs .NET, CodeQL, **audit des dépendances** NuGet (directes et transitives) et Python
  (`pip-audit`), Dependabot (NuGet, uv, actions, Docker), dépendances Python verrouillées (`uv.lock`).
- Images Docker : utilisateur non root, système de fichiers en lecture seule (compose), aucune clé dans l'image.
- Paquet caisse : empreinte SHA-256 publiée avec le zip ; installation en administrateur, mise à jour refusée caisse ouverte.

## Risques résiduels connus
| Risque | Mesure / décision |
|---|---|
| Poste caisse compromis (administrateur local) | La clé d'appareil (DPAPI machine) et les hachés de PIN en cache sont lisibles : BitLocker, compte Windows dédié non administrateur, révocation de la clé depuis le back-office |
| Débit en ligne sans ticket (rare) | Détecté par le rapprochement (alerte), contre-passation par la finance |
| Ventes hors ligne au-delà du découvert | Plafond hors ligne par badge (60 MAD), alerte « découvert hors ligne » |
| Licence Ultralytics (AGPL) | Décision juridique avant production (A82) |
