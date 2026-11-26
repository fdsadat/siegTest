namespace FiscalDocuments.Api;

public sealed record DevelopmentTokenResponse(string AccessToken, string TokenType, int ExpiresIn);
