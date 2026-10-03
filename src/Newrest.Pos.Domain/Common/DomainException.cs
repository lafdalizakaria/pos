namespace Newrest.Pos.Domain.Common;

/// <summary>Violation of a business rule. <see cref="Code"/> is stable and safe to expose to clients.</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    public DomainException()
    {
        Code = "domain_error";
    }

    public DomainException(string message) : base(message)
    {
        Code = "domain_error";
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
        Code = "domain_error";
    }

    public string Code { get; }
}

public static class Guard
{
    public static string NotBlank(string? value, string name, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("required", $"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException("too_long", $"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static decimal NotNegative(decimal value, string name)
    {
        if (value < 0)
        {
            throw new DomainException("negative_amount", $"{name} must not be negative.");
        }

        return value;
    }

    public static Guid NotEmpty(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("required", $"{name} is required.");
        }

        return value;
    }
}
