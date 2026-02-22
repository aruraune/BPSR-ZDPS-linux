using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.Managers.External;
using BPSR_ZDPS.Web;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZLinq;
using Zproto;

namespace BPSR_ZDPS
{
    public static class IntegrationManager
    {
        static ulong? LastReportedEncounterId;

        public static void InitBindings()
        {
            Log.Information("IntegrationManager InitBindings");

            EncounterManager.EncounterEndFinal += EncounterManager_EncounterEndFinal;

            BPTimerManager.InitializeBindings();
        }

        private static void EncounterManager_EncounterEndFinal(EncounterEndFinalData e)
        {
            // Hold onto a reference for the Encounter as once we enter the task it will no longer be the current one and may already be moved into the database
            var encounter = e.Encounter;

            Log.Information($"IntegrationManager EncounterManager_EncounterEndFinal [Reason = {e.Reason}] [BattleId {e.BattleId}] [EncounterId {e.EncounterId}]");

            // Only care about encounters with actual data in them
            if (!encounter.HasStatsBeenRecorded())
            {
                Log.Debug($"Encounter {encounter.EncounterId} has no recorded stats, stopping Report");
                return;
            }

            // We do not currently care about creating reports for benchmarks
            if (e.Reason == EncounterStartReason.BenchmarkEnd)
            {
                Log.Debug($"Encounter was a Benchmark, ignoring Report");
                return;
            }

            // Don't create reports for Null (Open World) states as we don't handle their encounters nicely yet
            if (encounter.DungeonState == EDungeonState.DungeonStateNull && e.Reason != EncounterStartReason.Restart && e.Reason != EncounterStartReason.NewObjective)
            {
                if (BattleStateMachine.DungeonStateHistory.Count > 0)
                {
                    var lastBSMDungeonState = BattleStateMachine.DungeonStateHistory.Last();
                    if (lastBSMDungeonState.Key != EDungeonState.DungeonStateNull)
                    {
                        Log.Debug($"Encounter DungeonState == EDungeonState.DungeonStateNull but BattleStateMachine.DungeonStateHistory reported it to actually be {lastBSMDungeonState}. Avoiding invalid Open World exit state.");
                    }
                    else
                    {
                        Log.Debug($"Encounter was reported as being in the Open World and we do not support it yet");
                        return;
                    }
                }
                else
                {
                    Log.Debug($"Encounter was reported as being in the Open World and we do not support it yet");
                    return;
                }
            }

            // We perform a check to make sure the setting is above 0 before iterating through the entity list to improve performance for most users who do not set a min count
            if (Settings.Instance.MinimumPlayerCountToCreateReport > 0)
            {
                if (encounter.Entities.Count(x => x.Value.EntityType == EEntityType.EntChar) < Settings.Instance.MinimumPlayerCountToCreateReport)
                {
                    return;
                }
            }

            bool forcePassConditions = false;
            if (Settings.Instance.AlwaysCreateReportAtDungeonEnd && e.Reason == EncounterStartReason.DungeonStateEnd)
            {
                forcePassConditions = true;
            }

            // Only create reports when there is a boss in the encounter and it is dead or the encounter is a wipe
            encounter.Entities.TryGetValue(encounter.BossUUID, out var bossEntity);
            var bossState = bossEntity?.GetAttrKV("AttrState");
            var bossHp = bossEntity?.GetAttrKV("AttrHp");
            var bossMaxHp = bossEntity?.GetAttrKV("AttrMaxHp");
            if (bossEntity != null)
            {
                Log.Debug($"BossAttrHp={bossHp}, BossAttrMaxHp={bossMaxHp}, HpPct={encounter.BossHpPct}");
            }
            if (e.Reason == EncounterStartReason.NewObjective || e.Reason == EncounterStartReason.Restart || forcePassConditions ||
                (encounter.BossUUID > 0 &&
                        (bossState != null &&
                            (bossEntity?.Hp == 0 || (EActorState)bossState == EActorState.ActorStateDead) ||
                            encounter.IsWipe)))
            {
                
                if (LastReportedEncounterId != null && LastReportedEncounterId == encounter.EncounterId)
                {
                    Log.Warning($"IntegrationManager was attempting to report the same EncounterId [{encounter.EncounterId}] twice in a row. Aborting the Report process.");
                    return;
                }

                Log.Debug($"IntegrationManager is creating an Encounter Report [Reason = {e.Reason}].");
                LastReportedEncounterId = encounter.EncounterId;

                HelperMethods.DeferredImGuiRenderAction = () =>
                {
                    if (encounter == null)
                    {
                        Log.Error($"IntegrationManager CreateReportImg had Encounter == Null! This should not happen. Aborting the Report process.");
                        return;
                    }

                    SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>? img;
#if WINDOWS
                    try
                    {
                        img = ReportImgGen.CreateReportImg(encounter);
                        if (img == null)
                        {
                            Log.Error($"IntegrationManager CreateReportImg returned a Null image object! Aborting the Report process.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Unexpected error in IntegrationManager CreateReportImg:\n{ex.Message}\nStack Trace:\n{ex.StackTrace}");
                        return;
                    }
#else
                    Log.Warning("Report generation is only available on Windows. Skipping report.");
                    return;
#endif
                    
                    Task.Factory.StartNew(() =>
                    {
                        if (Settings.Instance.WebhookReportsEnabled)
                        {
                            switch (Settings.Instance.WebhookReportsMode)
                            {
                                case EWebhookReportsMode.DiscordDeduplication:
                                case EWebhookReportsMode.FallbackDiscordDeduplication:
                                case EWebhookReportsMode.Discord:
                                    if (!string.IsNullOrEmpty(Settings.Instance.WebhookReportsDiscordUrl))
                                    {
                                        WebManager.SubmitReportToWebhook(encounter, img, Settings.Instance.WebhookReportsDiscordUrl);
                                    }
                                    else
                                    {
                                        Log.Error("IntegrationManager could not send report to Discord, URL was not set.");
                                    }
                                    break;
                                case EWebhookReportsMode.Custom:
                                    if (!string.IsNullOrEmpty(Settings.Instance.WebhookReportsCustomUrl))
                                    {
                                        WebManager.SubmitReportToWebhook(encounter, img, Settings.Instance.WebhookReportsCustomUrl);
                                    }
                                    else
                                    {
                                        Log.Error("IntegrationManager could not send report to Custom URL, URL was not set.");
                                    }
                                    break;
                            }
                        }
                    });
                };
            }
            else
            {
                Log.Information($"IntegrationManager EncounterEndFinal did not detect a dead boss or wipe in Battle: {e.BattleId} Encounter: {e.EncounterId}.");
                Log.Debug($"BossName:{encounter.BossName} BossUUID:{encounter.BossUUID}, BossHpPct:{encounter.BossHpPct}, IsWipe:{encounter.IsWipe}");
                if (bossState != null)
                {
                    Log.Debug($"BossState {(EActorState)bossState}");
                }
            }
        }
    }
}
