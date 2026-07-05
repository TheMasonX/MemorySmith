namespace MemorySmith.App.Services;

/// <summary>
/// Shared bootstrap-authorization gate used by both the local-password setup path
/// (<see cref="SecurityServices.CreateFirstAdminAsync"/>) and the OAuth callback path
/// (<see cref="Hosting.GitHubOAuthCallbackHandler.OnCreatingTicketAsync"/>).
///
/// Encapsulates the loopback + bootstrap-token check so both code paths apply
/// identical gating before promoting a first user to Admin.
/// </summary>
public static class BootstrapGate
{
    /// <summary>
    /// Authorizes a first-admin bootstrap attempt.
    /// </summary>
    /// <param name="httpContext">The current HTTP context (may be null in tests).</param>
    /// <param name="setup">Auth setup options (AllowLoopbackBootstrap, BootstrapTokenHash).</param>
    /// <param name="suppliedToken">Optional bootstrap token supplied by the caller.</param>
    /// <returns>A tuple indicating whether the request is authorized and, if not, an error message.</returns>
    public static (bool IsAuthorized, string? ErrorMessage) Authorize(
        HttpContext? httpContext,
        AuthSetupOptions setup,
        string? suppliedToken = null)
    {
        var isLoopback = MemorySmithRequestGuardMiddleware.IsLoopback(
            httpContext?.Connection.RemoteIpAddress);

        var tokenIsValid = ValidateBootstrapToken(suppliedToken, setup.BootstrapTokenHash);

        if (!isLoopback && !tokenIsValid)
        {
            return (false, "Initial setup is only available from localhost or with a valid bootstrap token.");
        }

        if (isLoopback && !setup.AllowLoopbackBootstrap && !tokenIsValid)
        {
            return (false, "Initial setup requires a valid bootstrap token.");
        }

        return (true, null);
    }

    private static bool ValidateBootstrapToken(string? token, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token.Trim())));
        return FixedTimeEquals(tokenHash, expectedHash.Trim());
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(actual.ToUpperInvariant());
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected.ToUpperInvariant());
        return actualBytes.Length == expectedBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
