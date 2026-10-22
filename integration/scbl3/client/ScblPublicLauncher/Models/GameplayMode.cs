namespace SplinterCellCNLauncher.Models;

public enum GameplayMode
{
    Official,
    Mod
}

public static class GameplayModeNames
{
    public const string Official = "Official";
    public const string Mod = "MOD";

    public static string ToName(GameplayMode mode)
        => mode == GameplayMode.Mod ? Mod : Official;

    public static bool TryParse(string? value, out GameplayMode mode)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.Equals(Mod, StringComparison.OrdinalIgnoreCase))
        {
            mode = GameplayMode.Mod;
            return true;
        }
        if (normalized.Equals(Official, StringComparison.OrdinalIgnoreCase))
        {
            mode = GameplayMode.Official;
            return true;
        }

        mode = GameplayMode.Official;
        return false;
    }

    public static GameplayMode ParseOrOfficial(string? value)
        => TryParse(value, out GameplayMode mode) ? mode : GameplayMode.Official;

    public static string NormalizeOrOfficial(string? value)
        => ToName(ParseOrOfficial(value));
}
