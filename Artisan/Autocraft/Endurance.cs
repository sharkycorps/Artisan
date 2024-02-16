﻿using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.Sounds;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.CircularBuffers;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;

namespace Artisan.Autocraft
{
    public class EnduranceIngredients
    {
        public int HQSet { get; set; }
        public int IngredientSlot { get; set; }
        public int NQSet { get; set; }
    }

    internal static unsafe class Endurance
    {
        internal static bool SkipBuffs = false;
        internal static List<Task> Tasks = new();
        internal static CircularBuffer<long> Errors = new(5);

        internal static List<int>? HQData = null;

        internal static ushort RecipeID = 0;

        internal static EnduranceIngredients[] SetIngredients = new EnduranceIngredients[6];

        internal static readonly List<uint> UnableToCraftErrors = new List<uint>()
        {
            1134,1135,1136,1137,1138,1139,1140,1141,1142,1143,1144,1145,1146,1148,1149,1198,1199,1222,1223,1224,
        };

        internal static bool Enable
        {
            get => enable;
            set
            {
                Tasks.Clear();
                enable = value;
            }
        }

        internal static string RecipeName
        {
            get => RecipeID == 0 ? "No Recipe Selected" : LuminaSheets.RecipeSheet[RecipeID].ItemResult.Value.Name.RawString.Trim();
        }

        internal static void ToggleEndurance(bool enable)
        {
            if (RecipeID > 0)
            {
                Enable = enable;
            }
        }

        internal static void Dispose()
        {
            Svc.Toasts.ErrorToast -= Toasts_ErrorToast;
            Svc.Toasts.ErrorToast -= CheckNonMaxQuantityModeFinished;
        }

        internal static void Draw()
        {
            if (CraftingListUI.Processing)
            {
                ImGui.TextWrapped("Processing list...");
                return;
            }

            ImGui.TextWrapped("Endurance mode is Artisan's way to repeat the same craft over and over, either so many times or until you run out of materials. It has full capabilities to automatically repair your gear once a piece is under a certain percentage, use food/potions/exp manuals and extract materia from spiritbonding. Please note these settings are independent of crafting list settings, and only intended to be used to craft the one item repeatedly.");
            ImGui.Separator();
            ImGui.Spacing();

            if (RecipeID == 0)
            {
                ImGuiEx.TextV(ImGuiColors.DalamudRed, "No recipe selected");
            }
            else
            {
                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                    ImGui.BeginDisabled();

                if (ImGui.Checkbox("Enable Endurance Mode", ref enable))
                {
                    ToggleEndurance(enable);
                }

                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                {
                    ImGui.EndDisabled();

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"You cannot start Endurance as you do not possess ingredients to craft this recipe.");
                        ImGui.EndTooltip();
                    }
                }

                ImGuiComponents.HelpMarker("In order to begin Endurance Mode crafting you should first select the recipe in the crafting menu.\nEndurance Mode will automatically repeat the selected recipe similar to Auto-Craft but will factor in food/medicine buffs before doing so.");

                ImGuiEx.Text($"Recipe: {RecipeName} {(RecipeID != 0 ? $"({LuminaSheets.ClassJobSheet[LuminaSheets.RecipeSheet[RecipeID].CraftType.Row + 8].Abbreviation})" : "")}");
            }

            bool repairs = P.Config.Repair;
            if (ImGui.Checkbox("Automatic Repairs", ref repairs))
            {
                P.Config.Repair = repairs;
                P.Config.Save();
            }
            ImGuiComponents.HelpMarker($"If enabled, Artisan will automatically repair your gear when any piece reaches the configured repair threshold.\n\nCurrent min gear condition is {RepairManager.GetMinEquippedPercent()}% and cost to repair at a vendor is {RepairManager.GetNPCRepairPrice()} gil.\n\nIf unable to repair with Dark Matter, will try for a nearby repair NPC.");
            if (P.Config.Repair)
            {
                //ImGui.SameLine();
                ImGui.PushItemWidth(200);
                int percent = P.Config.RepairPercent;
                if (ImGui.SliderInt("##repairp", ref percent, 10, 100, $"%d%%"))
                {
                    P.Config.RepairPercent = percent;
                    P.Config.Save();
                }
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
                ImGui.BeginDisabled();

            bool materia = P.Config.Materia;
            if (ImGui.Checkbox("Automatically Extract Materia", ref materia))
            {
                P.Config.Materia = materia;
                P.Config.Save();
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
            {
                ImGui.EndDisabled();

                ImGuiComponents.HelpMarker("This character has not unlocked materia extraction. This setting will be ignored.");
            }
            else
                ImGuiComponents.HelpMarker("Will automatically extract materia from any equipped gear once it's spiritbond is 100%");

            ImGui.Checkbox("Craft only X times", ref P.Config.CraftingX);
            if (P.Config.CraftingX)
            {
                ImGui.Text("Number of Times:");
                ImGui.SameLine();
                ImGui.PushItemWidth(200);
                if (ImGui.InputInt("###TimesRepeat", ref P.Config.CraftX))
                {
                    if (P.Config.CraftX < 0)
                        P.Config.CraftX = 0;
                }
            }

            if (ImGui.Checkbox("Use Quick Synthesis where possible", ref P.Config.QuickSynthMode))
            {
                P.Config.Save();
            }

            bool stopIfFail = P.Config.EnduranceStopFail;
            if (ImGui.Checkbox("Disable Endurance Mode Upon Failed Craft", ref stopIfFail))
            {
                P.Config.EnduranceStopFail = stopIfFail;
                P.Config.Save();
            }

            bool stopIfNQ = P.Config.EnduranceStopNQ;
            if (ImGui.Checkbox("Disable Endurance Mode Upon Crafting an NQ item", ref stopIfNQ))
            {
                P.Config.EnduranceStopNQ = stopIfNQ;
                P.Config.Save();
            }

            if (ImGui.Checkbox("Max Quantity Mode", ref P.Config.MaxQuantityMode))
            {
                P.Config.Save();
            }

            ImGuiComponents.HelpMarker("Will set ingredients for you, to maximise the amount of crafts possible.");
        }

        internal static void DrawRecipeData()
        {
            var addonPtr = Svc.GameGui.GetAddonByName("RecipeNote", 1);
            if (TryGetAddonByName<AddonRecipeNoteFixed>("RecipeNote", out var addon))
            {
                if (addonPtr == IntPtr.Zero)
                {
                    return;
                }

                if (addon->AtkUnitBase.IsVisible && addon->AtkUnitBase.UldManager.NodeListCount >= 49)
                {
                    try
                    {
                        if (addon->AtkUnitBase.UldManager.NodeList[88]->IsVisible)
                        {
                            RecipeID = 0;
                            return;
                        }

                        if (addon->SelectedRecipeName is null)
                            return;

                        var selectedRecipe = Operations.GetSelectedRecipeEntry();
                        if (selectedRecipe == null)
                        {
                            RecipeID = 0;
                            return;
                        }

                        if (addon->AtkUnitBase.UldManager.NodeList[49]->IsVisible)
                        {
                            RecipeID = selectedRecipe->RecipeId;
                        }
                        Array.Clear(SetIngredients);

                        for (int i = 0; i <= 5; i++)
                        {
                            try
                            {
                                var node = addon->AtkUnitBase.UldManager.NodeList[23 - i]->GetAsAtkComponentNode();
                                if (node->Component->UldManager.NodeListCount < 16)
                                    return;

                                if (node is null || !node->AtkResNode.IsVisible)
                                {
                                    break;
                                }

                                var hqSetButton = node->Component->UldManager.NodeList[6]->GetAsAtkComponentNode();
                                var nqSetButton = node->Component->UldManager.NodeList[9]->GetAsAtkComponentNode();

                                var hqSetText = hqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;
                                var nqSetText = nqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;

                                int hqSet = Convert.ToInt32(hqSetText.ToString().GetNumbers());
                                int nqSet = Convert.ToInt32(nqSetText.ToString().GetNumbers());

                                EnduranceIngredients ingredients = new EnduranceIngredients()
                                {
                                    IngredientSlot = i,
                                    HQSet = hqSet,
                                    NQSet = nqSet,
                                };

                                SetIngredients[i] = ingredients;
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex, "Setting Recipe ID");
                        RecipeID = 0;
                    }
                }
            }
        }

        internal static void Init()
        {
            Svc.Toasts.ErrorToast += Toasts_ErrorToast;
            Svc.Toasts.ErrorToast += CheckNonMaxQuantityModeFinished;
        }

        private static bool enable = false;
        private static void CheckNonMaxQuantityModeFinished(ref SeString message, ref bool isHandled)
        {
            if (!P.Config.MaxQuantityMode && Enable &&
                (message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1147).Text.ExtractText() ||
                 message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1146).Text.ExtractText() ||
                 message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1145).Text.ExtractText() ||
                 message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1144).Text.ExtractText()))
            {
                if (P.Config.PlaySoundFinishEndurance)
                    SoundPlayer.PlaySound();

                ToggleEndurance(false);
            }
        }

        public static void Update()
        {
            if (!Enable) return;
            var needToRepair = P.Config.Repair && RepairManager.GetMinEquippedPercent() < P.Config.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            if ((Crafting.CurState == Crafting.State.QuickCraft && Crafting.QuickSynthCompleted) || needToRepair || IPC.IPC.StopCraftingRequest ||
                (P.Config.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (!P.TM.IsBusy && Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
            {
                var isCrafting = Svc.Condition[ConditionFlag.Crafting];
                var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
                var recipe = LuminaSheets.RecipeSheet[RecipeID];
                if (PreCrafting.Tasks.Count > 0)
                {
                    return;
                }

                if (P.Config.CraftingX && P.Config.CraftX == 0 || PreCrafting.GetNumberCraftable(recipe) == 0)
                {
                    Enable = false;
                    P.Config.CraftingX = false;
                    DuoLog.Information("Craft X has completed.");
                    if (P.Config.PlaySoundFinishEndurance)
                        SoundPlayer.PlaySound();

                    return;
                }

                if (RecipeID == 0)
                {
                    Svc.Toasts.ShowError("No recipe has been set for Endurance mode. Disabling Endurance mode.");
                    DuoLog.Error("No recipe has been set for Endurance mode. Disabling Endurance mode.");
                    Enable = false;
                    return;
                }

                if ((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.Row + 8 != CharacterInfo.JobID)
                {
                    PreCrafting.equipGearsetLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.Row + 8), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                bool needEquipItem = recipe.ItemRequired.Row > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.Row);
                if (needEquipItem)
                {
                    PreCrafting.equipAttemptLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.Row), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (!Spiritbond.ExtractMateriaTask(P.Config.Materia))
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (P.Config.Repair && !RepairManager.ProcessRepair())
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                var config = P.Config.RecipeConfigs.GetValueOrDefault(RecipeID) ?? new();
                PreCrafting.CraftType type = P.Config.QuickSynthMode && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;
                bool needConsumables = (type == PreCrafting.CraftType.Normal || (type == PreCrafting.CraftType.Quick && P.Config.UseConsumablesQuickSynth)) && (!ConsumableChecker.IsFooded(config) || !ConsumableChecker.IsPotted(config) || !ConsumableChecker.IsManualled(config) || !ConsumableChecker.IsSquadronManualled(config));
                bool hasConsumables = config != default ? ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && ConsumableChecker.HasItem(config.RequiredManual, false) && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) : true;

                if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
                {
                    DuoLog.Error($"Can't craft {recipe.ItemResult.Value?.Name}: required consumables not up");
                    Enable = false;
                    return;
                }

                if (needConsumables)
                {
                    if (!P.TM.IsBusy && !PreCrafting.Occupied())
                    {
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                        P.TM.DelayNext(100);
                    }
                    return;
                }

                if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
                {
                    if (!P.TM.IsBusy)
                    {
                        PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(200)));

                        if (!CraftingListFunctions.RecipeWindowOpen()) return;

                        if (type == PreCrafting.CraftType.Quick)
                        {
                            P.TM.Enqueue(() => Operations.QuickSynthItem(P.Config.CraftingX ? P.Config.CraftX : 99), "EnduranceQSStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2500, "EnduranceQSWaitStart");
                        }
                        else if (type == PreCrafting.CraftType.Normal)
                        {
                            P.TM.DelayNext(200);
                            if (P.Config.MaxQuantityMode)
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(), "EnduranceSetIngredientsNonLayout");
                            else
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(SetIngredients), "EnduranceSetIngredientsLayout");

                            P.TM.Enqueue(() => Operations.RepeatActualCraft(), "EnduranceNormalStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2500, "EnduranceNormalWaitStart");
                        }
                    }

                }
            }
        }

        private static void Toasts_ErrorToast(ref SeString message, ref bool isHandled)
        {
            if (Enable || CraftingListUI.Processing)
            {
                foreach (uint errorId in UnableToCraftErrors)
                {
                    if (message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == errorId).Text.ExtractText())
                    {
                        Svc.Toasts.ShowError($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                        DuoLog.Error($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                        if (enable)
                            Enable = false;
                        if (CraftingListUI.Processing)
                            CraftingListFunctions.Paused = true;
                        PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                        P.TM.Abort();
                        CraftingListFunctions.CLTM.Abort();
                    }
                }

                Errors.PushBack(Environment.TickCount64);
                Svc.Log.Warning($"Error Warnings [{Errors.Count(x => x > Environment.TickCount64 - 10 * 1000)}]: {message}");
                if (Errors.Count() >= 5 && Errors.All(x => x > Environment.TickCount64 - 10 * 1000))
                {
                    Svc.Toasts.ShowError($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to too many errors in succession.");
                    DuoLog.Error($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to too many errors in succession.");
                    if (enable)
                        Enable = false;
                    if (CraftingListUI.Processing)
                        CraftingListFunctions.Paused = true;
                    Errors.Clear();
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                }
            }
        }
    }
}
