using CursedApp.Models;
using CursedApp.Services.Providers;

namespace CursedApp.Services;

/// <summary>
/// Ties the disk scan to the metadata provider: enumerate Interface\AddOns,
/// fingerprint each folder, then let the provider group and version them.
/// </summary>
public sealed class AddonManager(
    AddonFolderScanner scanner,
    IAddonProvider provider,
    AddonInstaller installer,
    ILogSink log)
{
    /// <summary>Folders WoW ships itself; they are not user addons.</summary>
    private static readonly HashSet<string> BlizzardFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Blizzard_AchievementUI", "Blizzard_APIDocumentation", "Blizzard_ArchaeologyUI",
        "Blizzard_ArenaUI", "Blizzard_AuctionHouseUI", "Blizzard_AzeriteEssenceUI",
        "Blizzard_AzeriteRespecUI", "Blizzard_AzeriteUI", "Blizzard_BarbershopUI",
        "Blizzard_BattlefieldMap", "Blizzard_BehavioralMessaging", "Blizzard_BindingUI",
        "Blizzard_BlackMarketUI", "Blizzard_BoostTutorial", "Blizzard_Calendar",
        "Blizzard_ChallengesUI", "Blizzard_CharacterCustomize", "Blizzard_ChromieTimeUI",
        "Blizzard_ClickBindingUI", "Blizzard_ClassTalentUI", "Blizzard_ClassTrialUpgradeUI",
        "Blizzard_ClientSavedVariables", "Blizzard_Collections", "Blizzard_CombatLog",
        "Blizzard_CombatText", "Blizzard_Communities", "Blizzard_CompactUnitFrames",
        "Blizzard_Console", "Blizzard_Contribution", "Blizzard_CovenantCallings",
        "Blizzard_CovenantPreviewUI", "Blizzard_CovenantRenown", "Blizzard_CovenantSanctum",
        "Blizzard_CovenantToasts", "Blizzard_CraftingOrders", "Blizzard_CUFProfiles",
        "Blizzard_DeathRecap", "Blizzard_DebugTools", "Blizzard_Deprecated",
        "Blizzard_DelvesCompanionConfiguration", "Blizzard_DelvesDashboardUI",
        "Blizzard_EditMode", "Blizzard_EncounterJournal", "Blizzard_EventTrace",
        "Blizzard_ExpansionLandingPage", "Blizzard_FlightMap", "Blizzard_FrameXML",
        "Blizzard_FrameXMLBase", "Blizzard_FrameXMLUtil", "Blizzard_GameRules",
        "Blizzard_GarrisonTemplates", "Blizzard_GarrisonUI", "Blizzard_GenericTraitUI",
        "Blizzard_GMChatUI", "Blizzard_GuildBankUI", "Blizzard_GuildControlUI",
        "Blizzard_GuildRecruitmentUI", "Blizzard_GuildUI", "Blizzard_HelpPlate",
        "Blizzard_InspectUI", "Blizzard_ItemInteractionUI", "Blizzard_ItemSocketingUI",
        "Blizzard_ItemUpgradeUI", "Blizzard_LandingSoulbinds", "Blizzard_LookingForGuildUI",
        "Blizzard_MacroUI", "Blizzard_MageArcaneChargesBar", "Blizzard_MajorFactions",
        "Blizzard_MapCanvas", "Blizzard_MawBuffs", "Blizzard_MenuTemplates",
        "Blizzard_MonkHarmonyBar", "Blizzard_MoneyReceipt", "Blizzard_MovePad",
        "Blizzard_NamePlates", "Blizzard_ObjectAPI", "Blizzard_ObjectiveTracker",
        "Blizzard_OrderHallUI", "Blizzard_PerksProgram", "Blizzard_PetBattleUI",
        "Blizzard_PlayerChoice", "Blizzard_PlayerSpells", "Blizzard_ProfessionsCustomerOrders",
        "Blizzard_Professions", "Blizzard_ProfessionsTemplates", "Blizzard_PTRFeedback",
        "Blizzard_PVPMatch", "Blizzard_PVPUI", "Blizzard_QuestNavigation",
        "Blizzard_RaidUI", "Blizzard_RuneforgeUI", "Blizzard_ScrappingMachineUI",
        "Blizzard_Settings", "Blizzard_SettingsDefinitions", "Blizzard_SharedMapDataProviders",
        "Blizzard_SharedTalentUI", "Blizzard_SharedXML", "Blizzard_SharedXMLBase",
        "Blizzard_SharedXMLGame", "Blizzard_SocialUI", "Blizzard_Soulbinds",
        "Blizzard_SubscriptionInterstitialUI", "Blizzard_TalentUI", "Blizzard_TalkingHeadUI",
        "Blizzard_TimeManager", "Blizzard_TokenUI", "Blizzard_TorghastLevelPicker",
        "Blizzard_TradeSkillUI", "Blizzard_Tutorial", "Blizzard_TutorialBase",
        "Blizzard_UIPanels_Game", "Blizzard_UIParent", "Blizzard_UIWidgets",
        "Blizzard_UnitFrame", "Blizzard_VoidStorageUI", "Blizzard_WarboardUI",
        "Blizzard_WeeklyRewards", "Blizzard_WorldMap", "Blizzard_WowTokenUI",
    };

    public IAddonProvider Provider => provider;

    /// <summary>
    /// Scans the client's addon folder and returns the grouped, versioned list.
    /// </summary>
    public async Task<IReadOnlyList<InstalledAddon>> GetInstalledAsync(
        WowInstallation installation,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(installation.AddonsPath))
        {
            log.Warn($"Addon folder does not exist: {installation.AddonsPath}");
            return [];
        }

        progress?.Report("Scanning addon folders…");

        var directories = EnumerateAddonDirectories(installation.AddonsPath);
        var scanStarted = System.Diagnostics.Stopwatch.StartNew();

        // Fingerprinting reads every file an addon loads — several hundred for a
        // large one — so the scan is I/O bound and worth running in parallel.
        var scanned = new System.Collections.Concurrent.ConcurrentBag<AddonFolder>();
        var completed = 0;

        await Parallel.ForEachAsync(
            directories,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
            },
            async (directory, token) =>
            {
                var folder = await scanner.ScanAsync(directory, installation.Flavor, token).ConfigureAwait(false);

                // A folder with no toc is not a loadable addon (usually a leftover).
                if (folder.Toc is not null)
                    scanned.Add(folder);

                var done = Interlocked.Increment(ref completed);
                if (done % 10 == 0)
                    progress?.Report($"Scanning addon folders… {done}/{directories.Count}");
            }).ConfigureAwait(false);

        var folders = scanned
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        log.Info($"Scanned {folders.Count} addon folders in {scanStarted.ElapsedMilliseconds} ms ({installation.AddonsPath})");

        progress?.Report(provider.IsConfigured
            ? $"Matching {folders.Count} folders against {provider.Name}…"
            : "Reading local addon data…");

        var matchStarted = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.MatchInstalledAsync(folders, installation.Flavor, ct).ConfigureAwait(false);
        log.Info($"Matched {result.Count} addons in {matchStarted.ElapsedMilliseconds} ms.");

        return result;
    }

    public Task<IReadOnlyList<AddonSearchResult>> SearchAsync(
        string query,
        WowInstallation installation,
        CancellationToken ct = default) =>
        provider.SearchAsync(query, installation.Flavor, ct);

    public Task<AddonRelease?> GetLatestReleaseAsync(
        string providerId,
        WowInstallation installation,
        CancellationToken ct = default) =>
        provider.GetLatestReleaseAsync(providerId, installation.Flavor, ct);

    public Task<IReadOnlyList<string>> InstallAsync(
        AddonRelease release,
        WowInstallation installation,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        installer.InstallAsync(release, installation, progress, ct);

    /// <summary>Unpacks a zip the user downloaded themselves.</summary>
    public Task<IReadOnlyList<string>> InstallFromArchiveAsync(
        string archivePath,
        WowInstallation installation,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        // Extraction is synchronous file work; keep it off the UI thread.
        Task.Run(() => installer.InstallFromArchive(archivePath, installation, progress), ct);

    private static List<string> EnumerateAddonDirectories(string addonsPath)
    {
        try
        {
            return
            [
                .. Directory.EnumerateDirectories(addonsPath)
                    .Where(path =>
                    {
                        var name = Path.GetFileName(path);
                        return !BlizzardFolders.Contains(name)
                            && !name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
