# Déploiement du service vision (poste caisse Windows)

Prérequis sur le poste : [uv](https://docs.astral.sh/uv/) (installe Python 3.12 si besoin) et
[NSSM](https://nssm.cc/) dans le `PATH` (ou passer `-Uv` / `-Nssm`). Exécuter en administrateur :

```powershell
# mode démo / pilote sans clé
.\install-vision-service.ps1 -Source C:\temp\vision -Provider mock
# Gemini (la clé est saisie masquée, jamais écrite dans un fichier)
.\install-vision-service.ps1 -Source C:\temp\vision -Provider gemini -GeminiApiKey (Read-Host -AsSecureString "Clé Gemini")
```

Le service `NewrestPosVision` écoute sur `http://127.0.0.1:8765` (refus de démarrer sur une autre interface sauf
`VISION_ALLOW_REMOTE=true`). Changer de provider sans redéployer la caisse :
`C:\ProgramData\Newrest\POS\Vision\vision.json` → `{"provider": "gemini"}` (pris en compte à la requête suivante).
Contrôle : `Invoke-RestMethod http://127.0.0.1:8765/health`. Journaux : `C:\ProgramData\Newrest\POS\Vision\logs`.
