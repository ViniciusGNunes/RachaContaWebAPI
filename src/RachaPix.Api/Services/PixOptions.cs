namespace RachaPix.Api.Services;

public sealed class PixOptions
{
    /// <summary>Receiver city used for member Pix codes (members have no city).</summary>
    public string DefaultCity { get; set; } = "BRASIL";
}
