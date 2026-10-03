# Déploiement du service vision (poste caisse Windows)

Prérequis sur le poste : [uv](https://docs.astral.sh/uv/) (installe Python 3.12 si besoin) et
[NSSM](https://nssm.cc/) dans le `PATH` (ou passer `-Uv` / `-Nssm`). Exécuter en administrateur :

```powershell
# mode démo / pilote sans clé
.\install-vision-service.ps1 -Source C:\temp\vision -Provider mock
# Gemini (clé saisie masquée, écrite chiffrée DPAPI « machine » dans C:\ProgramData\Newrest\POS\Vision\gemini.key)
.\install-vision-service.ps1 -Source C:\temp\vision -Provider gemini -GeminiApiKey (Read-Host -AsSecureString "Clé Gemini")
```

Le service `NewrestPosVision` écoute sur `http://127.0.0.1:8765` (refus de démarrer sur une autre interface sauf
`VISION_ALLOW_REMOTE=true`). Le moteur (Gemini, YOLO, hybride) et le modèle YOLO se choisissent **par site dans le
back-office** (page *Modèles vision*) : la caisse télécharge le modèle, l'installe dans le service et le bascule
(`runtime.json`). En secours, `C:\ProgramData\Newrest\POS\Vision\vision.json` → `{"provider": "gemini"}` force une valeur
localement (prise en compte à la requête suivante ; signalée en back-office « forcé localement »).
Contrôle : `Invoke-RestMethod http://127.0.0.1:8765/health`. Journaux : `C:\ProgramData\Newrest\POS\Vision\logs`.

Rotation de la clé Gemini : relancer le script avec `-GeminiApiKey` (le fichier est réécrit, relu automatiquement).
Caméra : `VISION_CAMERA_INDEX`, `VISION_CAMERA_CROP=x,y,largeur,hauteur` (zone du plateau **uniquement**, vérifiée à
l'installation : aucun visage dans le champ), `VISION_CAMERA_EXPOSURE`, `VISION_CAMERA_WHITE_BALANCE` — à ajouter dans
`vision.json` (ex. `{"camera_crop": "320,180,1280,720"}`), pris en compte à la capture suivante.
