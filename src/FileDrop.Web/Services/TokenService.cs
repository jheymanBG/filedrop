using System.Security.Cryptography;

namespace FileDrop.Web.Services;

public interface ITokenService
{
    string CreateToken(int bytes = 32);
}

public sealed class TokenService : ITokenService
{
    public string CreateToken(int bytes = 32)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }
}
