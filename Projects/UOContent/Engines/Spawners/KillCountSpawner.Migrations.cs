namespace Server.Engines.Spawners;

public partial class KillCountSpawner
{
    // Version 0 had neither a boss timeout nor a kill decay; both stay off until set or re-imported.
    private void MigrateFrom(V0Content content)
    {
        _countedSpawners = content.CountedSpawners;
        _killsRequired = content.KillsRequired;
        _killCount = content.KillCount;
    }
}
