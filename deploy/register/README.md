# Paquet d'installation d'une caisse

Contenu de `newrest-pos-register-<version>.zip` (construit par `deploy/register/build-package.sh`, aussi produit par la CI) :
`caisse/` (application WPF autonome, .NET inclus), `vision/` (service de reconnaissance), `install-register.ps1`,
`install-vision-service.ps1`, `VERSION`.

Prérequis du poste : Windows 10/11 x64, [uv](https://docs.astral.sh/uv/) et [NSSM](https://nssm.cc/) (service vision),
pilote de l'imprimante, caméra USB.

```powershell
# administrateur, dans le dossier décompressé
.\install-register.ps1 -ServerUrl https://pos.newrest.ma/ -Printer EscPosSpooler -PrinterTarget "EPSON TM-T20III" -AutoStart `
                       -GeminiApiKey (Read-Host -AsSecureString "Clé Gemini")
```

Mise à jour : fermer la caisse (après la clôture), relancer le script du nouveau paquet : application remplacée, données,
clé d'appareil et `appsettings.local.json` conservés. Déploiement en masse : Intune (application Win32) ou SCCM avec la
même commande ; vérifier ensuite la version dans le back-office (**Supervision**, colonne Version).
