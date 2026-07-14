using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DeathExile;

public class DeathExileModSystem : ModSystem
{
    private Harmony Patcher { get; set; }
    private static ModConfig Config { get; set; }
    private static ICoreServerAPI Api { get; set; }

    private static readonly HashSet<string> EndingExile = new();

    private enum PunishState
    {
        None = 0,
        TemporarySpectator = 1,
        Exile = 2
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        if (Harmony.HasAnyPatches(Mod.Info.ModID)) return;

        Api = api;

        Patcher = new Harmony(Mod.Info.ModID);
        Patcher.PatchCategory(Mod.Info.ModID);

        TryToLoadConfig(api);

        // Предупреждаем админа, если выбран Exile, но точка не задана
        if (Config.PunishmentMode == PunishmentMode.Exile
            && (Config.ExileX <= 0 || Config.ExileY <= 0 || Config.ExileZ <= 0))
        {
            api.Logger.Warning("[deathexile] PunishmentMode = Exile, но координаты изгнания не заданы. "
                + "Задайте ExileX/Y/Z в deathexileConfig.json или встаньте в нужном месте и вызовите /lives setexile, "
                + "иначе изгнанные игроки останутся на обычном спавне.");
        }

        api.Event.PlayerDeath += OnPlayerDeath;
        api.Event.PlayerRespawn += OnPlayerRespawn;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // Раз в 10 секунд проверяем таймеры у онлайн-игроков:
        // ежечасные сообщения об остатке и освобождение по истечении срока
        api.Event.RegisterGameTickListener(OnTick, 10000);

        api.ChatCommands
            .Create("lives")
            .WithDescription("Show your current remaining lives.")
            .RequiresPrivilege("chat")
            .HandleWith(OnLivesCommand)
            .BeginSubCommand("pardon")
                .WithDescription("Admin: end a player's punishment as if the time had passed.")
                .RequiresPrivilege("controlserver")
                .WithArgs(api.ChatCommands.Parsers.Word("playername"))
                .HandleWith(OnPardonCommand)
            .EndSubCommand()
            .BeginSubCommand("setexile")
                .WithDescription("Admin: set the exile destination to your current position.")
                .RequiresPrivilege("controlserver")
                .HandleWith(OnSetExileCommand)
            .EndSubCommand();
    }

    private void TryToLoadConfig(ICoreServerAPI api)
    {
        try
        {
            Config = api.LoadModConfig<ModConfig>("deathexileConfig.json") ?? new ModConfig();
            api.StoreModConfig(Config, "deathexileConfig.json");
        }
        catch (Exception e)
        {
            Mod.Logger.Error(Lang.Get("deathexile:logs.config_load_error"));
            Mod.Logger.Error(e);
            Config = new ModConfig();
        }
    }

    // Команды

    private static TextCommandResult OnLivesCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("Command requires server player.", "");
        }

        string message = Lang.Get("deathexile:command_response.lives", GetLives(player));

        var state = GetPunishState(player);
        if (state != PunishState.None)
        {
            var remaining = RemainingTime(player, state);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            message += "\n" + Lang.Get(RemainingKey(state), (int)remaining.TotalHours, remaining.Minutes);
        }

        return TextCommandResult.Success(message);
    }

    // Админ: завершить наказание игрока так, будто его срок истёк
    private static TextCommandResult OnPardonCommand(TextCommandCallingArgs args)
    {
        if (Config == null) return TextCommandResult.Error("Config not loaded.");

        string name = ((string)args[0])?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return TextCommandResult.Error(Lang.Get("deathexile:command_response.pardon_notfound", name ?? ""));
        }

        // Сначала ищем онлайн
        IServerPlayer target = null;
        bool online = false;
        foreach (var p in Api.World.AllOnlinePlayers)
        {
            if (p.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase) && p is IServerPlayer sp)
            {
                target = sp;
                online = true;
                break;
            }
        }

        // Иначе разрешаем по последнему известному имени и грузим объект игрока
        if (target == null)
        {
            var data = Api.PlayerData.GetPlayerDataByLastKnownName(name);
            if (data == null)
            {
                return TextCommandResult.Error(Lang.Get("deathexile:command_response.pardon_notfound", name));
            }

            target = Api.World.PlayerByUid(data.PlayerUID) as IServerPlayer;
            if (target == null)
            {
                return TextCommandResult.Error(Lang.Get("deathexile:command_response.pardon_offline_unavailable", name));
            }
        }

        var state = GetPunishState(target);

        // Временное наказание (спектатор/изгнание) - истекаем как по таймеру
        if (state == PunishState.TemporarySpectator || state == PunishState.Exile)
        {
            ExpirePunishment(target);

            if (online)
            {
                CheckPunishment(target, notifyNow: false); // завершится прямо сейчас
                Api.Logger.Notification("[deathexile] {0} pardoned (online).", name);
                return TextCommandResult.Success(Lang.Get("deathexile:command_response.pardon_done", name));
            }

            // Оффлайн: завершится при следующем входе - идентично реальному истечению
            Api.Logger.Notification("[deathexile] {0} pardoned (offline, ends on login).", name);
            return TextCommandResult.Success(Lang.Get("deathexile:command_response.pardon_offline", name));
        }

        // Пожизненный спектатор (нет метки состояния): в спектаторе и без жизней.
        if (IsPermanentlyBanned(target))
        {
            PardonPermanent(target);
            Api.Logger.Notification("[deathexile] {0} pardoned (permanent spectator).", name);
            return TextCommandResult.Success(Lang.Get("deathexile:command_response.pardon_done", name));
        }

        //Нечего завершать
        return TextCommandResult.Error(Lang.Get("deathexile:command_response.pardon_none", name));
    }

    // Админ: записать точку изгнания = текущая позиция вызывающего
    private static TextCommandResult OnSetExileCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("Command requires server player.", "");
        }
        if (Config == null) return TextCommandResult.Error("Config not loaded.");

        var pos = player.Entity.Pos;
        Config.ExileX = pos.X;
        Config.ExileY = pos.Y;
        Config.ExileZ = pos.Z;

        Api.StoreModConfig(Config, "deathexileConfig.json");
        Api.Logger.Notification("[deathexile] Exile point set to {0:0}, {1:0}, {2:0} by {3}.",
            pos.X, pos.Y, pos.Z, player.PlayerName);

        return TextCommandResult.Success(Lang.Get("deathexile:command_response.exile_set",
            (int)pos.X, (int)pos.Y, (int)pos.Z));
    }

    // Жизни

    private static int GetLives(IServerPlayer player)
    {
        return player.WorldData.GetModData("deathexile:lives", Config.InitialLivesAmount);
    }

    private static void SetLives(IServerPlayer player, int value)
    {
        player.WorldData.SetModData("deathexile:lives", value);
    }

    public static void AddLife(IServerPlayer player)
    {
        // Во время наказания жизни не начисляем - иначе шестерёнка, использованная в изгнании, дала бы жизнь, которая всё равно сотрётся при сбросе счётчика

        if (GetPunishState(player) != PunishState.None) return;

        var newLives = GetLives(player) + Config.LivesPerGear;

        if (newLives >= Config.MaxLivesAmount && Config.MaxLivesAmount != -1)
        {
            newLives = Config.MaxLivesAmount;
        }

        SetLives(player, newLives);
        Notify(player, "deathexile:lives.added", newLives);
    }

    // Смерть / респавн

    private static void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        if (EndingExile.Contains(player.PlayerUID)) return;

        var lives = GetLives(player);
        if (lives <= 0) return;

        SetLives(player, lives - 1);

        if (GetLives(player) > 0)
        {
            Notify(player, "deathexile:lives.lost", GetLives(player));
        }
    }

    private static void OnPlayerRespawn(IServerPlayer player)
    {
        // Респавн после завершения изгнания: игрок вернулся на обычный спавн со сброшенным счётчиком жизней - больше делать нечего

        if (EndingExile.Remove(player.PlayerUID)) return;

        // Игрок умер, УЖЕ отбывая наказание. Возвращаем его в наказание, не сбрасывая таймер, чтобы нельзя было сбежать/обнулить срок смертью
        
        var state = GetPunishState(player);
        if (state != PunishState.None)
        {
            ReapplyPunishment(player, state);
            return;
        }

        // Жизни ещё есть - обычный респавн.
        if (GetLives(player) > 0) return;

        // Жизни кончились - запускаем наказание из конфига.
        StartPunishment(player);
    }

    private static void OnPlayerNowPlaying(IServerPlayer player)
    {
        // Пересчёт при заходе. Если срок вышел, пока игрок был оффлайн, -
        // освобождаем сейчас; иначе показываем, сколько осталось.
        CheckPunishment(player, notifyNow: true);
    }

    private static void OnTick(float dt)
    {
        if (Config == null) return;

        foreach (var online in Api.World.AllOnlinePlayers)
        {
            if (online is IServerPlayer player)
            {
                CheckPunishment(player, notifyNow: false);
            }
        }
    }

    // Машина состояний наказания

    private static void StartPunishment(IServerPlayer player)
    {
        switch (Config.PunishmentMode)
        {
            case PunishmentMode.PermanentSpectator:
                SetSpectator(player);
                Notify(player, "deathexile:respawn.spectator", GetLives(player));
                break;

            case PunishmentMode.TemporarySpectator:
                StampPunishment(player, PunishState.TemporarySpectator);
                SetSpectator(player);
                NotifyRemaining(player, PunishState.TemporarySpectator, "deathexile:punish.spectator_start");
                break;

            case PunishmentMode.Exile:
                StampPunishment(player, PunishState.Exile);
                SetExileMovement(player);
                TeleportToExile(player);
                NotifyRemaining(player, PunishState.Exile, "deathexile:punish.exile_start");
                break;
        }
    }

    // Игрок умер, уже отбывая наказание: возвращаем режим движения/позицию, не трогая метку времени
    
    private static void ReapplyPunishment(IServerPlayer player, PunishState state)
    {
        switch (state)
        {
            case PunishState.TemporarySpectator:
                SetSpectator(player);
                break;

            case PunishState.Exile:
                SetExileMovement(player);
                TeleportToExile(player);
                break;
        }
    }

    // Главная проверка: сравнивает "сейчас" с сохранённой меткой. Освобождает по истечении срока; иначе (раз в час) пишет остаток

    private static void CheckPunishment(IServerPlayer player, bool notifyNow)
    {
        var state = GetPunishState(player);
        if (state == PunishState.None) return;

        var remaining = RemainingTime(player, state);

        if (remaining <= TimeSpan.Zero)
        {
            EndPunishment(player, state);
            return;
        }

        // Ежечасные сообщения: печатаем при смене номера часа-остатка.
        // Метку последнего показанного часа храним в памяти игрока, поэтому после перезахода не спамим повтором за тот же час.

        int hoursLeft = (int)Math.Ceiling(remaining.TotalHours);
        int lastShown = player.WorldData.GetModData("deathexile:punish_lasthour", -1);

        if (notifyNow || hoursLeft != lastShown)
        {
            player.WorldData.SetModData("deathexile:punish_lasthour", hoursLeft);
            NotifyRemaining(player, state, RemainingKey(state));
        }
    }

    private static void EndPunishment(IServerPlayer player, PunishState state)
    {
        ClearPunishment(player);

        switch (state)
        {
            case PunishState.TemporarySpectator:
                // Обратно в выживание + СБРОС счётчика: игрок начинает копить наказание заново, как будто только зашёл на сервер
                // 
                SetSurvival(player);
                SetLives(player, Config.InitialLivesAmount);
                Notify(player, "deathexile:punish.spectator_end", GetLives(player));
                break;

            case PunishState.Exile:
                // Помечаем, что убиваем ЭТОГО игрока намеренно, - чтобы
                // OnPlayerDeath не снял жизнь, а OnPlayerRespawn не запустил
                // наказание заново
                EndingExile.Add(player.PlayerUID);

                // Сбрасываем счётчик ЗАРАНЕЕ, до смерти
                SetLives(player, Config.InitialLivesAmount);

                // Возвращаем нормальный режим движения ДО смерти, чтобы после
                // респавна на спавне игрок не остался с "изгнанническими" флагами
                SetSurvival(player);

                Notify(player, "deathexile:punish.exile_end", GetLives(player));

                // Убиваем - ванильный респавн вернёт игрока на обычный спавн
                player.Entity?.Die(EnumDespawnReason.Death, new DamageSource
                {
                    Source = EnumDamageSource.Unknown,
                    Type = EnumDamageType.Injury
                });
                break;
        }
    }

    // Помилование

    // Двигаем метку старта далеко в прошлое, чтобы RemainingTime стал <= 0
    // при любой настроенной длительности. Дальше обычный CheckPunishment завершает
    // наказание штатно (онлайн - сейчас, оффлайн - при входе; как реальное истечение)
    private static void ExpirePunishment(IServerPlayer player)
    {
        player.WorldData.SetModData("deathexile:punish_startticks", 1L);
        player.WorldData.SetModData("deathexile:punish_lasthour", -1);
    }

    // Пожизненный бан не имеет метки состояния: игрок застрял в спектаторе без жизней
    // Обычный админ-наблюдатель держит жизни > 0, поэтому ложных срабатываний нет,
    // пока InitialLivesAmount >= 1
    private static bool IsPermanentlyBanned(IServerPlayer player)
    {
        return player.WorldData.CurrentGameMode == EnumGameMode.Spectator
            && GetLives(player) <= 0;
    }

    private static void PardonPermanent(IServerPlayer player)
    {
        SetSurvival(player);
        SetLives(player, Config.InitialLivesAmount);
        Notify(player, "deathexile:punish.spectator_end", GetLives(player));
    }

    // Режимы игрока

    private static void SetSpectator(IServerPlayer player)
    {
        player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
        player.WorldData.NoClip = true;
        player.WorldData.FreeMove = true;
        player.BroadcastPlayerData();
    }

    private static void SetSurvival(IServerPlayer player)
    {
        player.WorldData.CurrentGameMode = EnumGameMode.Survival;
        player.WorldData.NoClip = false;
        player.WorldData.FreeMove = false;
        player.BroadcastPlayerData();
    }

    // Изгнанник остаётся в выживании (может умирать, есть, мёрзнуть - это часть
    // наказания), но без NoClip/FreeMove.
    private static void SetExileMovement(IServerPlayer player)
    {
        player.WorldData.CurrentGameMode = EnumGameMode.Survival;
        player.WorldData.NoClip = false;
        player.WorldData.FreeMove = false;
        player.BroadcastPlayerData();
    }

    // Телепорт в заданную админом точку. Отложен на ~300 мс, чтобы движок,
    // ставящий игрока на точку спавна при респавне, не перебил наш телепорт
    // (иначе игрок то в изгнании, то на нормальном спавне - та самая "неоднородность")
    private static void TeleportToExile(IServerPlayer player)
    {
        double x = Config.ExileX, y = Config.ExileY, z = Config.ExileZ;
        var mapSize = Api.World.BlockAccessor.MapSize;

        bool valid = x > 0 && y > 0 && z > 0
            && x < mapSize.X && y < mapSize.Y && z < mapSize.Z;

        if (!valid)
        {
            Api.Logger.Warning("[deathexile] Координаты изгнания ({0:0},{1:0},{2:0}) не заданы или вне карты - "
                + "{3} оставлен на обычном спавне. Задайте ExileX/Y/Z в конфиге или вызовите /lives setexile.",
                x, y, z, player.PlayerName);
            return;
        }

        Api.Event.RegisterCallback(dt =>
        {
            if (player?.Entity == null) return;
            // Если за время задержки игрока помиловали/освободили - не телепортируем
            if (GetPunishState(player) != PunishState.Exile) return;
            player.Entity.TeleportToDouble(x, y, z);
        }, 300);
    }

    // Хранение метки времени в памяти игрока

    // Записываем момент начала наказания как абсолютный UtcNow.Ticks. Всё
    // остальное - производная от разницы с текущим временем, поэтому отсчёт
    // идёт в реальном времени и переживает выход игрока и рестарт сервера
    private static void StampPunishment(IServerPlayer player, PunishState state)
    {
        player.WorldData.SetModData("deathexile:punish_state", (int)state);
        player.WorldData.SetModData("deathexile:punish_startticks", DateTime.UtcNow.Ticks);
        player.WorldData.SetModData("deathexile:punish_lasthour", -1);
    }

    private static PunishState GetPunishState(IServerPlayer player)
    {
        return (PunishState)player.WorldData.GetModData("deathexile:punish_state", (int)PunishState.None);
    }

    private static void ClearPunishment(IServerPlayer player)
    {
        player.WorldData.SetModData("deathexile:punish_state", (int)PunishState.None);
        player.WorldData.SetModData("deathexile:punish_startticks", 0L);
        player.WorldData.SetModData("deathexile:punish_lasthour", -1);
    }

    private static TimeSpan RemainingTime(IServerPlayer player, PunishState state)
    {
        long startTicks = player.WorldData.GetModData("deathexile:punish_startticks", 0L);
        var started = new DateTime(startTicks, DateTimeKind.Utc);
        var elapsed = DateTime.UtcNow - started;

        double totalHours = state == PunishState.Exile
            ? Config.ExileRealHours
            : Config.SpectatorRealHours;

        return TimeSpan.FromHours(totalHours) - elapsed;
    }

    // Уведомления

    private static string RemainingKey(PunishState state)
    {
        return state == PunishState.Exile
            ? "deathexile:punish.exile_remaining"
            : "deathexile:punish.spectator_remaining";
    }

    private static void NotifyRemaining(IServerPlayer player, PunishState state, string key)
    {
        var remaining = RemainingTime(player, state);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        Notify(player, key, (int)remaining.TotalHours, remaining.Minutes);
    }

    private static void Notify(IServerPlayer player, string key, params object[] args)
    {
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get(key, args), EnumChatType.Notification);
    }

    public override void Dispose()
    {
        Patcher?.UnpatchAll(Mod.Info.ModID);
    }
}