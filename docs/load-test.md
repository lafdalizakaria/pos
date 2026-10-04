# Tests de charge (phase 6)

Outil : `tools/Newrest.Pos.LoadTest` — N caisses virtuelles en parallèle contre l'API : jeton de caisse, référence complète,
ouverture de session, puis par plateau consultation de badge, **débit de compte en ligne** (2 plateaux sur 3), **ticket chaîné**
(construit avec le code Domain, empreinte contrôlée par le serveur), battement de cœur. **Jamais contre la production.**

```bash
dotnet run --project tools/Newrest.Pos.LoadTest -c Release -- --api https://pos-recette.newrest.ma --dev-signing-key <clé de recette> \
       --registers 60 --trays 20 --think-ms 2000 --report resultats.md
```

## Banc de mesure (2026-10-04)

API en conteneur (image de production, `Release`) **limitée à 2 vCPU / 1 Go**, SQL Server 2022 en conteneur, générateur
de charge sur la même machine (4 cœurs) : chiffres **pessimistes** par rapport à une infrastructure dédiée.

Référence métier : pointe de midi ≈ 60 caisses × 4 plateaux/min ≈ **240 plateaux/min** pour l'ensemble des sites.

### Résultats — 60 caisses × 20 plateaux, pause 2000 ms

Durée 50,7 s — 1200 plateaux — **23,7 plateaux/s** (1420 /min)

| Opération | Appels | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
|---|---:|---:|---:|---:|---:|
| auth/register-token | 60 | 208 | 355 | 454 | 454 |
| register/account-movements (débit) | 802 | 10 | 57 | 632 | 729 |
| register/badges | 1200 | 8 | 53 | 432 | 570 |
| register/cash-sessions | 60 | 336 | 515 | 662 | 662 |
| register/heartbeat | 120 | 8 | 137 | 156 | 171 |
| register/reference (complet) | 60 | 440 | 955 | 1069 | 1069 |
| register/tickets | 1200 | 9 | 36 | 403 | 535 |

Refus métier attendus (solde insuffisant, etc.) : aucun

Erreurs : **aucune**

### Résultats — 40 caisses × 30 plateaux, pause 0 ms

Durée 8,5 s — 1200 plateaux — **141,8 plateaux/s** (8508 /min)

| Opération | Appels | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
|---|---:|---:|---:|---:|---:|
| auth/register-token | 40 | 40 | 59 | 63 | 63 |
| register/account-movements (débit) | 798 | 131 | 294 | 367 | 450 |
| register/badges | 1200 | 73 | 127 | 150 | 187 |
| register/cash-sessions | 40 | 105 | 134 | 153 | 153 |
| register/heartbeat | 120 | 46 | 84 | 93 | 109 |
| register/reference (complet) | 40 | 134 | 187 | 210 | 210 |
| register/tickets | 1200 | 76 | 146 | 255 | 347 |

Refus métier attendus (solde insuffisant, etc.) : aucun

Erreurs : **aucune**

### Résultats — 100 caisses × 50 plateaux, pause 0 ms

Durée 30,0 s — 5000 plateaux — **166,5 plateaux/s** (9990 /min)

| Opération | Appels | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
|---|---:|---:|---:|---:|---:|
| auth/register-token | 100 | 88 | 170 | 178 | 229 |
| register/account-movements (débit) | 3274 | 384 | 1133 | 1322 | 1459 |
| register/badges | 5000 | 78 | 148 | 289 | 488 |
| register/cash-sessions | 100 | 236 | 440 | 504 | 506 |
| register/heartbeat | 500 | 44 | 134 | 206 | 228 |
| register/reference (complet) | 100 | 307 | 723 | 745 | 760 |
| register/tickets | 5000 | 83 | 659 | 1077 | 1411 |

Refus métier attendus (solde insuffisant, etc.) : aucun

Erreurs : **aucune**

## Analyse

- **Pointe réaliste ×6** (60 caisses, un plateau toutes les 2 s par caisse = 1 420 plateaux/min) : p95 < 60 ms pour le badge, le
  débit et le ticket ; aucune erreur. La marge sur la pointe réelle (240/min) est d'environ **×40** au débit maximal mesuré (~10 000 plateaux/min).
- **Stress** (100 caisses sans pause) : aucune erreur ni perte ; le débit de compte monte à p95 1,1 s car le test concentre
  tous les débits sur **8 comptes** de démonstration (verrou de ligne `UPDLOCK` volontaire : les débits d'un même compte sont
  sérialisés). En réel, un convive ne passe qu'un plateau à la fois : cette contention n'existe pas.
- **Intégrité après charge** : vérification de toutes les chaînes (218 caisses, 7 838 tickets) en **0,9 s**, aucune anomalie ;
  extrapolation : 1 million de tickets ≈ 2 min pour la tâche nocturne.
- Mémoire API stable (≈ 250 Mo sur 1 Go).
- **Défaut trouvé et corrigé** : la limite de demandes de jeton (20/min par adresse IP) bloquait les caisses d'un même site
  derrière une même adresse publique (NAT) au démarrage simultané → 429 et passage hors ligne. Limite portée à 300/min/IP,
  configurable (`RateLimits:RegisterTokenPerMinute`) ; la sécurité repose sur la clé d'appareil de 256 bits.

## À refaire avant la mise en production
Sur l'infrastructure cible (SQL Server de production, réseau des sites, reverse proxy TLS) avec le volume réel de convives
(comptes distincts) et une durée longue (1 h) pour observer la mémoire et les journaux.
