namespace SplinterCellCNLauncher.Services;

// Only the launcher's default log-directory dependency is replaced. Every test
// passes an owned temporary root to the actual CredentialStore; accidental use
// of a machine/user directory fails before touching it. DPAPI is never mocked.
internal static class LogService
{
    public static string AppDataDir => throw new InvalidOperationException("Tests must supply their owned credential root.");
}
