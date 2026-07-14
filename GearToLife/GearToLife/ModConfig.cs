using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DeathExile;

[JsonConverter(typeof(StringEnumConverter))]
public enum PunishmentMode
{
    PermanentSpectator, // спектатор навсегда (текущее поведение)
    TemporarySpectator, // спектатор на N часов, затем обратно в выживание
    Exile               // изгнание в заданную точку на N часов, затем смерть и спавн
}

public class ModConfig
{
    public int InitialLivesAmount = 1;
    public int MaxLivesAmount = -1;
    public int LivesPerGear = 1;

    public string PunishmentMode_Help =
        "PunishmentMode options: " +
        "PermanentSpectator = spectator forever | " +
        "TemporarySpectator = spectator for SpectatorRealHours, then back to survival | " +
        "Exile = teleport to ExileX/Y/Z for ExileRealHours, then die and respawn at normal spawn. /lives setexile - set the exile location for players";

    public PunishmentMode PunishmentMode = PunishmentMode.PermanentSpectator;

    public double SpectatorRealHours = 24;
    public double ExileRealHours = 24;

    public double ExileX = 0;
    public double ExileY = 0;
    public double ExileZ = 0;
}