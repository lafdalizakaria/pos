using System.Globalization;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.BackOffice.Ui;

/// <summary>French user-facing texts for business error codes (Arabic resources to be added with RTL support).</summary>
public static class Messages
{
    private static readonly Dictionary<string, string> ByCode = new()
    {
        ["forbidden"] = "Vous n'avez pas les droits nécessaires pour cette action.",
        ["not_found"] = "Élément introuvable (il a peut-être été supprimé).",
        ["conflict"] = "Ce code ou ce numéro est déjà utilisé.",
        ["insufficient_funds"] = "Solde insuffisant : l'opération dépasserait le découvert autorisé.",
        ["idempotency_conflict"] = "Cette opération a déjà été enregistrée avec d'autres valeurs.",
        ["already_reversed"] = "Ce mouvement a déjà été annulé.",
        ["refund_exceeds_balance"] = "Le remboursement ne peut pas dépasser le solde positif du compte.",
        ["use_credit_note"] = "Une consommation s'annule par un avoir sur le ticket, pas par une contre-passation.",
        ["invalid_amount"] = "Montant invalide (2 décimales maximum).",
        ["negative_amount"] = "Le montant ne peut pas être négatif.",
        ["required"] = "Un champ obligatoire est vide.",
        ["too_long"] = "Un champ dépasse la longueur autorisée.",
        ["invalid_pin_format"] = "Le PIN doit contenir 4 à 8 chiffres.",
        ["invalid_file"] = "Fichier refusé : format ou contenu invalide.",
        ["immutable_field"] = "Ce champ ne peut plus être modifié.",
        ["badge_already_active"] = "Le convive a déjà un badge actif : déclarez-le perdu pour le remplacer.",
        ["empty_menu"] = "Un menu vide ne peut pas être publié.",
        ["menu_published"] = "Dépubliez le menu avant de le supprimer.",
        ["duplicate_menu_item"] = "Cet article est déjà au menu.",
        ["invalid_validity"] = "Période de validité incohérente.",
        ["invalid_subsidy"] = "Règle de subvention invalide.",
        ["invalid_vat_rate"] = "Taux de TVA invalide (fraction entre 0 et 1, ex. 0,10).",
        ["checksum_mismatch"] = "Le fichier du modèle ne correspond pas à son manifeste (taille ou empreinte SHA-256).",
        ["invalid_manifest"] = "manifest.json invalide : utiliser celui produit par l'entraînement.",
        ["invalid_model"] = "Modèle invalide (classes, taille d'entrée ou empreinte).",
        ["invalid_version"] = "Version : 1 à 32 lettres, chiffres, « . », « _ » ou « - ».",
        ["model_retired"] = "Un modèle retiré ne peut pas être republié : importer une nouvelle version.",
        ["model_in_use"] = "Ce modèle est utilisé par un site : choisir d'abord un autre modèle.",
        ["no_published_model"] = "Aucun modèle publié : publier un modèle avant de choisir YOLO ou hybride.",
        ["model_not_published"] = "Seul un modèle publié peut être déployé.",
        ["invalid_thresholds"] = "Seuils invalides : 0 ≤ seuil bas ≤ seuil haut ≤ 1.",
        ["period_not_closed"] = "Mois pas encore archivable : attendre quelques jours après la fin du mois (synchronisation des caisses).",
        ["integrity_failed"] = "Chaîne d'intégrité rompue sur une caisse : le mois ne peut pas être scellé (voir Supervision).",
        ["archive_key_missing"] = "Aucune clé de signature des archives n'est configurée (Archive:SigningKeyPem).",
        ["archive_missing"] = "Le fichier de l'archive est introuvable dans le stockage.",
        ["cutoff_too_recent"] = "La date doit être antérieure d'au moins 3 mois (facturation, contestations).",
        ["use_credit_note"] = "Une consommation s'annule par un avoir sur son ticket (contre-passation possible seulement pour un débit sans ticket).",
    };

    public static string For(Exception exception) => exception switch
    {
        DomainException d when ByCode.TryGetValue(d.Code, out var text) => text,
        DomainException d => $"Opération refusée : {d.Message}",
        _ => "Erreur technique. Réessayez ou contactez le support.",
    };

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-MA");

    public static string Mad(decimal amount) => amount.ToString("#,##0.00", French) + " MAD";

    public static string Percent(decimal rate) => (rate * 100).ToString("0.##", French) + " %";

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", French);

    public static string DateTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Africa/Casablanca")).ToString("dd/MM/yyyy HH:mm", French);

    public static string Status(bool active) => active ? "Actif" : "Inactif";
}
