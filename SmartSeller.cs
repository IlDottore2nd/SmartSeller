using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEngine;

namespace SmartSeller
{
    [BepInPlugin("lucas.smartseller", "Smart Seller", "2.0.0")]
    public class SmartSellerPlugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony("lucas.smartseller");

        internal static Func<bool> ShouldProtectUtilityItems = delegate { return true; };

        private void Awake()
        {
            var protectUtilityItemsConfig = Config.Bind(
                "Whitelist",
                "WhitelistUtilityItems",
                true,
                "When enabled, Smart Seller will ignore kitchen knives, shotguns, shotgun shells, and keys."
            );

            ShouldProtectUtilityItems = delegate
            {
                return protectUtilityItemsConfig.Value;
            };

            harmony.PatchAll();

            Logger.LogInfo("Smart Seller 1.0.10 loaded.");
        }
    }

    [HarmonyPatch(typeof(Terminal), "QuitTerminal")]
    public class TerminalQuitPatch
    {
        private static void Postfix()
        {
            TerminalPatch.CancelPendingSellFromTerminalExit();
        }
    }

    [HarmonyPatch(typeof(Terminal), "LoadNewNode")]
    public class TerminalHelpPatch
    {
        private static void Prefix(TerminalNode __0)
        {
            TerminalPatch.TryAppendSmartSellerHelpToNode(__0);
        }
    }

    [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
    public class TerminalPatch
    {
        private static List<GrabbableObject> pendingSellItems = new List<GrabbableObject>();
        private static int pendingSellTarget = 0;
        private static int pendingSellTotal = 0;
        private static string pendingSellMode = "";

        private class ItemValue
        {
            public GrabbableObject Item;
            public int EffectiveValue;
            public int RawValue;
        }

        private class CombinationResult
        {
            public List<GrabbableObject> Items = new List<GrabbableObject>();
            public int Total;
            public int RawTotal;
        }

        private static bool Prefix(
            Terminal __instance,
            ref TerminalNode __result,
            int ___textAdded,
            TMP_InputField ___screenText)
        {
            string text;

            try
            {
                if (object.ReferenceEquals(___screenText, null))
                    return true;

                if (___textAdded <= 0)
                    return true;

                int startIndex = ___screenText.text.Length - ___textAdded;

                if (startIndex < 0)
                    return true;

                text = ___screenText.text
                    .Substring(startIndex)
                    .Trim()
                    .ToLower();
            }
            catch
            {
                return true;
            }

            if (HasPendingSell())
            {
                if (text == "c" || text == "confirm")
                {
                    __result = CreateTerminalNode(ConfirmPendingSell());
                    return false;
                }

                if (text == "d" || text == "deny" || text == "cancel")
                {
                    __result = CreateTerminalNode(DenyPendingSell());
                    return false;
                }

                __result = CreateTerminalNode("Pending sell active.\nPress C to confirm sell or D to deny sell.\n\n");
                return false;
            }

            if (text == "sell" || text.StartsWith("sell "))
            {
                string response = HandleSellCommand(text);
                __result = CreateTerminalNode(response);
                return false;
            }

            return true;
        }

        public static void TryAppendSmartSellerHelpToNode(TerminalNode node)
        {
            try
            {
                if (object.ReferenceEquals(node, null))
                    return;

                if (string.IsNullOrEmpty(node.displayText))
                    return;

                string currentText = node.displayText;
                string lowered = currentText.ToLower();

                if (lowered.Contains("smart seller"))
                    return;

                bool looksLikeMainHelp =
                    lowered.Contains("store") &&
                    lowered.Contains("moons") &&
                    lowered.Contains("bestiary") &&
                    lowered.Contains("storage");

                if (!looksLikeMainHelp)
                    return;

                string smartSellerHelp =
                    "\n\n>SMART SELLER\n" +
                    "sell <quota>, sell <max>, sell <value>\n" +
                    "Examples: sell quota, sell max, sell 500\n";

                node.displayText = currentText.TrimEnd() + smartSellerHelp;
            }
            catch
            {
            }
        }

        public static void CancelPendingSellFromTerminalExit()
        {
            if (HasPendingSell())
                ClearPendingSell();
        }

        private static string HandleSellCommand(string text)
        {
            if (!IsAtCompany())
                return "You must be at the Company Building.\n\n";

            string[] split = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (split.Length < 2)
                return "Usage: sell <amount>, sell quota, sell min, or sell max\n\n";

            string argument = split[1].ToLower();

            return PrepareSell(argument);
        }

        private static TerminalNode CreateTerminalNode(string message)
        {
            TerminalNode node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = message;
            node.clearPreviousText = true;
            return node;
        }

        private static bool IsAtCompany()
        {
            try
            {
                if (StartOfRound.Instance == null)
                    return false;

                if (StartOfRound.Instance.currentLevel == null)
                    return false;

                SelectableLevel level = StartOfRound.Instance.currentLevel;

                if (level.levelID == 3)
                    return true;

                string planetName = level.PlanetName;

                if (!string.IsNullOrEmpty(planetName))
                {
                    string loweredName = planetName.ToLower();

                    if (loweredName.Contains("company"))
                        return true;

                    if (loweredName.Contains("gordion"))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string PrepareSell(string argument)
        {
            float multiplier = GetCurrentSellMultiplier();

            List<GrabbableObject> allShipObjects = UnityEngine.Object
                .FindObjectsOfType<GrabbableObject>()
                .Where(x =>
                    x != null &&
                    x.isInShipRoom &&
                    x.scrapValue > 0 &&
                    !x.isHeld)
                .ToList();

            if (allShipObjects.Count == 0)
                return "No scrap found.\n\n";

            List<GrabbableObject> shipObjects = allShipObjects
                .Where(x => !IsProtectedUtilityItem(x))
                .ToList();

            if (shipObjects.Count == 0)
            {
                if (SmartSellerPlugin.ShouldProtectUtilityItems())
                    return "No eligible scrap found.\nUtility item whitelist is enabled, so knives, shotguns, shells, and keys were ignored.\n\n";

                return "No eligible scrap found.\n\n";
            }

            int target = 0;
            string mode = argument;
            List<GrabbableObject> bestMatch = new List<GrabbableObject>();

            if (argument == "quota" || argument == "min")
            {
                target = GetQuotaRemaining();

                if (target <= 0)
                    return "Quota is already fulfilled.\n\n";

                bestMatch = FindBestCombination(shipObjects, target, multiplier, true);
            }
            else if (argument == "max")
            {
                bestMatch = shipObjects;
                target = CalculateTotal(bestMatch, multiplier);
            }
            else
            {
                if (!int.TryParse(argument, out target))
                    return "Invalid command. Use: sell <amount>, sell quota, sell min, or sell max\n\n";

                if (target <= 0)
                    return "Sell amount must be higher than 0.\n\n";

                bestMatch = FindBestCombination(shipObjects, target, multiplier, false);
            }

            if (bestMatch.Count == 0)
                return "Could not find any matching scrap.\n\n";

            int selectedTotal = CalculateTotal(bestMatch, multiplier);

            SetPendingSell(bestMatch, target, selectedTotal, mode);

            string output = "";

            foreach (GrabbableObject item in bestMatch)
            {
                int value = Mathf.RoundToInt(item.scrapValue * multiplier);
                string itemName = GetItemName(item);

                output += "Selected: " + itemName + " ($" + value + ")\n";
            }

            output += "\nTarget: $" + target + " | Selected total: $" + selectedTotal + "\n";
            output += BuildProfitPreview(selectedTotal);
            output += "\nC to confirm sell | D to deny sell\n\n";

            return output;
        }

        private static string ConfirmPendingSell()
        {
            if (!HasPendingSell())
                return "No pending sell to confirm.\n\n";

            if (!IsAtCompany())
            {
                ClearPendingSell();
                return "Sell cancelled. You are no longer at the Company Building.\n\n";
            }

            if (NetworkManager.Singleton == null)
                return "Could not access NetworkManager. Sell was not confirmed.\n\n";

            if (!NetworkManager.Singleton.IsHost && !NetworkManager.Singleton.IsServer)
                return "Only the host can confirm automatic selling in this version.\n\n";

            DepositItemsDesk desk = UnityEngine.Object.FindObjectOfType<DepositItemsDesk>();

            if (desk == null)
                return "Could not find the Company sell desk. Sell was not confirmed.\n\n";

            pendingSellItems.RemoveAll(x =>
                x == null ||
                x.scrapValue <= 0 ||
                x.isHeld ||
                IsProtectedUtilityItem(x));

            if (pendingSellItems.Count == 0)
            {
                ClearPendingSell();
                return "Pending sell expired. No valid scrap left to sell.\n\n";
            }

            try
            {
                if (desk.itemsOnCounter == null)
                    return "Could not access the Company counter list. Sell was not confirmed.\n\n";

                desk.itemsOnCounter.RemoveAll(x => x == null);

                if (desk.itemsOnCounter.Count > 0)
                    return "The Company counter already has items on it. Clear the counter first, then confirm again.\n\n";

                for (int i = 0; i < pendingSellItems.Count; i++)
                {
                    GrabbableObject item = pendingSellItems[i];

                    PrepareItemForCompanyDesk(item, desk, i);

                    if (!desk.itemsOnCounter.Contains(item))
                        desk.itemsOnCounter.Add(item);
                }

                List<GrabbableObject> soldItems = new List<GrabbableObject>(pendingSellItems);

                int finalTotal = CalculateTotal(soldItems, GetCurrentSellMultiplier());
                int itemCount = soldItems.Count;
                string profitPreview = BuildProfitPreview(finalTotal);

                desk.SellItemsOnServer();

                RemoveSoldItemsFromWorld(soldItems);

                desk.itemsOnCounter.RemoveAll(x => x == null);

                ClearPendingSell();

                return "Confirmed sell.\nSold " + itemCount + " selected item(s).\nTotal value: $" + finalTotal + "\n" + profitPreview + "\n";
            }
            catch (Exception e)
            {
                return "Sell failed before confirmation finished.\nError: " + e.Message + "\n\n";
            }
        }

        private static string DenyPendingSell()
        {
            ClearPendingSell();
            return "Sell denied. Returning to terminal.\n\n";
        }

        private static void PrepareItemForCompanyDesk(GrabbableObject item, DepositItemsDesk desk, int index)
        {
            if (item == null)
                return;

            item.isInShipRoom = false;
            item.isInElevator = false;
            item.hasHitGround = true;

            item.gameObject.SetActive(true);
        }

        private static void RemoveSoldItemsFromWorld(List<GrabbableObject> soldItems)
        {
            if (soldItems == null)
                return;

            for (int i = 0; i < soldItems.Count; i++)
            {
                GrabbableObject item = soldItems[i];

                if (item == null)
                    continue;

                try
                {
                    item.isInShipRoom = false;
                    item.isInElevator = false;
                    item.scrapValue = 0;
                    item.SetScrapValue(0);
                }
                catch
                {
                }

                try
                {
                    NetworkObject networkObject = item.GetComponent<NetworkObject>();

                    if (networkObject != null && networkObject.IsSpawned)
                    {
                        networkObject.Despawn(true);
                        continue;
                    }
                }
                catch
                {
                }

                try
                {
                    item.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(item.gameObject);
                }
                catch
                {
                }
            }
        }

        private static bool HasPendingSell()
        {
            return pendingSellItems != null && pendingSellItems.Count > 0;
        }

        private static void SetPendingSell(
            List<GrabbableObject> items,
            int target,
            int total,
            string mode)
        {
            pendingSellItems = new List<GrabbableObject>(items);
            pendingSellTarget = target;
            pendingSellTotal = total;
            pendingSellMode = mode;
        }

        private static void ClearPendingSell()
        {
            pendingSellItems.Clear();
            pendingSellTarget = 0;
            pendingSellTotal = 0;
            pendingSellMode = "";
        }

        private static int GetQuotaRemaining()
        {
            try
            {
                int remaining = TimeOfDay.Instance.profitQuota - TimeOfDay.Instance.quotaFulfilled;

                if (remaining < 0)
                    remaining = 0;

                return remaining;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetQuotaFulfilled()
        {
            try
            {
                return TimeOfDay.Instance.quotaFulfilled;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetProfitQuota()
        {
            try
            {
                return TimeOfDay.Instance.profitQuota;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetDaysUntilDeadline()
        {
            try
            {
                return TimeOfDay.Instance.daysUntilDeadline;
            }
            catch
            {
                return 0;
            }
        }

        private static string BuildProfitPreview(int sellValue)
        {
            try
            {
                int currentFulfilled = GetQuotaFulfilled();
                int quota = GetProfitQuota();
                int daysLeft = GetDaysUntilDeadline();

                if (quota <= 0)
                    return "Profit bonus preview unavailable.\n";

                int afterSellFulfilled = currentFulfilled + sellValue;

                int currentBonus = CalculateProfitBonus(currentFulfilled, quota, daysLeft);
                int afterBonus = CalculateProfitBonus(afterSellFulfilled, quota, daysLeft);

                int bonusAddedByThisSell = afterBonus - currentBonus;

                if (bonusAddedByThisSell < 0)
                    bonusAddedByThisSell = 0;

                int totalValueFromThisSell = sellValue + bonusAddedByThisSell;
                int totalQuotaProfitAfterSell = afterSellFulfilled + afterBonus;

                string output = "";

                output += "\nProfit quota preview:\n";
                output += "Quota after sell: $" + afterSellFulfilled + " / $" + quota + "\n";

                if (afterSellFulfilled < quota)
                {
                    int stillNeeded = quota - afterSellFulfilled;

                    output += "Still needed for quota: $" + stillNeeded + "\n";
                    output += "Profit bonus after sell: $0\n";
                    output += "Expected value from this sell: $" + sellValue + "\n";

                    return output;
                }

                int overQuota = afterSellFulfilled - quota;
                int dayModifier = 15 * daysLeft;

                output += "Over quota by: $" + overQuota + "\n";
                output += "Day efficiency modifier: " + FormatSignedMoney(dayModifier) + "\n";
                output += "Profit bonus after sell: $" + afterBonus + "\n";
                output += "Bonus added by this sell: +" + bonusAddedByThisSell + "\n";
                output += "Expected value from this sell: $" + sellValue + " + $" + bonusAddedByThisSell + " = $" + totalValueFromThisSell + "\n";
                output += "Total quota profit after sell: $" + totalQuotaProfitAfterSell + "\n";

                return output;
            }
            catch
            {
                return "Profit bonus preview unavailable.\n";
            }
        }

        private static string FormatSignedMoney(int value)
        {
            if (value > 0)
                return "+$" + value;

            if (value < 0)
                return "-$" + Math.Abs(value);

            return "$0";
        }

        private static int CalculateProfitBonus(int quotaFulfilledValue, int profitQuota, int daysUntilDeadline)
        {
            if (quotaFulfilledValue < profitQuota)
                return 0;

            int overQuota = quotaFulfilledValue - profitQuota;

            float rawBonus = ((float)overQuota / 5f) + (15f * daysUntilDeadline);
            int bonus = Mathf.FloorToInt(rawBonus);

            if (bonus < 0)
                bonus = 0;

            return bonus;
        }

        private static float GetCurrentSellMultiplier()
        {
            try
            {
                if (StartOfRound.Instance != null)
                    return StartOfRound.Instance.companyBuyingRate;
            }
            catch
            {
            }

            try
            {
                int daysLeft = TimeOfDay.Instance.daysUntilDeadline;

                if (daysLeft == 0)
                    return 1.0f;

                if (daysLeft == 1)
                    return 0.77f;

                if (daysLeft == 2)
                    return 0.55f;

                return 0.33f;
            }
            catch
            {
                return 1.0f;
            }
        }

        private static int CalculateTotal(List<GrabbableObject> items, float multiplier)
        {
            int total = 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null)
                    continue;

                total += Mathf.RoundToInt(items[i].scrapValue * multiplier);
            }

            return total;
        }

        private static List<GrabbableObject> FindBestCombination(
            List<GrabbableObject> items,
            int target,
            float multiplier,
            bool preferAtLeastTarget)
        {
            List<ItemValue> valuedItems = new List<ItemValue>();

            for (int i = 0; i < items.Count; i++)
            {
                GrabbableObject item = items[i];

                if (item == null)
                    continue;

                int effectiveValue = Mathf.RoundToInt(item.scrapValue * multiplier);

                if (effectiveValue <= 0)
                    continue;

                ItemValue itemValue = new ItemValue();
                itemValue.Item = item;
                itemValue.EffectiveValue = effectiveValue;
                itemValue.RawValue = item.scrapValue;

                valuedItems.Add(itemValue);
            }

            valuedItems = valuedItems
                .OrderBy(x => x.EffectiveValue)
                .ThenBy(x => GetItemName(x.Item))
                .ToList();

            if (valuedItems.Count == 0)
                return new List<GrabbableObject>();

            int totalAll = 0;
            int maxSingle = 0;

            for (int i = 0; i < valuedItems.Count; i++)
            {
                totalAll += valuedItems[i].EffectiveValue;

                if (valuedItems[i].EffectiveValue > maxSingle)
                    maxSingle = valuedItems[i].EffectiveValue;
            }

            int searchCap = totalAll;

            if (target > 0 && maxSingle > 0)
                searchCap = Math.Min(totalAll, target + maxSingle);

            Dictionary<int, CombinationResult> combinations = new Dictionary<int, CombinationResult>();

            CombinationResult empty = new CombinationResult();
            empty.Total = 0;
            empty.RawTotal = 0;

            combinations[0] = empty;

            for (int i = 0; i < valuedItems.Count; i++)
            {
                ItemValue itemValue = valuedItems[i];

                List<KeyValuePair<int, CombinationResult>> snapshot = combinations.ToList();

                for (int j = 0; j < snapshot.Count; j++)
                {
                    int previousTotal = snapshot[j].Key;
                    CombinationResult previousCombination = snapshot[j].Value;

                    int newTotal = previousTotal + itemValue.EffectiveValue;

                    if (newTotal > searchCap)
                        continue;

                    CombinationResult newCombination = new CombinationResult();
                    newCombination.Total = newTotal;
                    newCombination.RawTotal = previousCombination.RawTotal + itemValue.RawValue;
                    newCombination.Items = new List<GrabbableObject>(previousCombination.Items);
                    newCombination.Items.Add(itemValue.Item);

                    CombinationResult existingCombination;

                    if (!combinations.TryGetValue(newTotal, out existingCombination))
                    {
                        combinations[newTotal] = newCombination;
                    }
                    else if (IsBetterSameTotalCombination(newCombination, existingCombination))
                    {
                        combinations[newTotal] = newCombination;
                    }
                }
            }

            CombinationResult best = null;

            foreach (CombinationResult combination in combinations.Values)
            {
                if (combination == null)
                    continue;

                if (combination.Total <= 0)
                    continue;

                if (best == null || IsBetterCandidate(combination, best, target, preferAtLeastTarget))
                    best = combination;
            }

            if (best == null)
                return new List<GrabbableObject>();

            return best.Items;
        }

        private static bool IsBetterSameTotalCombination(CombinationResult challenger, CombinationResult current)
        {
            if (challenger.Items.Count != current.Items.Count)
                return challenger.Items.Count < current.Items.Count;

            if (challenger.RawTotal != current.RawTotal)
                return challenger.RawTotal < current.RawTotal;

            return false;
        }

        private static bool IsBetterCandidate(
            CombinationResult challenger,
            CombinationResult current,
            int target,
            bool preferAtLeastTarget)
        {
            if (preferAtLeastTarget)
            {
                bool challengerMeetsTarget = challenger.Total >= target;
                bool currentMeetsTarget = current.Total >= target;

                if (challengerMeetsTarget && !currentMeetsTarget)
                    return true;

                if (!challengerMeetsTarget && currentMeetsTarget)
                    return false;

                if (challengerMeetsTarget && currentMeetsTarget)
                {
                    if (challenger.Total != current.Total)
                        return challenger.Total < current.Total;
                }
                else
                {
                    if (challenger.Total != current.Total)
                        return challenger.Total > current.Total;
                }

                return IsBetterSameTotalCombination(challenger, current);
            }
            else
            {
                int challengerDifference = Math.Abs(target - challenger.Total);
                int currentDifference = Math.Abs(target - current.Total);

                if (challengerDifference != currentDifference)
                    return challengerDifference < currentDifference;

                if (challenger.Total != current.Total)
                    return challenger.Total < current.Total;

                return IsBetterSameTotalCombination(challenger, current);
            }
        }

        private static bool IsProtectedUtilityItem(GrabbableObject item)
        {
            try
            {
                if (!SmartSellerPlugin.ShouldProtectUtilityItems())
                    return false;

                string itemName = GetItemName(item)
                    .ToLower()
                    .Replace("-", " ")
                    .Replace("_", " ")
                    .Trim();

                if (string.IsNullOrEmpty(itemName))
                    return false;

                if (itemName.Contains("kitchen") && itemName.Contains("knife"))
                    return true;

                if (itemName == "knife" || itemName.Contains(" knife") || itemName.Contains("knife "))
                    return true;

                if (itemName.Contains("shotgun"))
                    return true;

                if (itemName.Contains("shell") && itemName.Contains("shotgun"))
                    return true;

                if (itemName == "key" || itemName.EndsWith(" key") || itemName.Contains(" key "))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string GetItemName(GrabbableObject item)
        {
            try
            {
                if (item != null && item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.itemName))
                    return item.itemProperties.itemName;
            }
            catch
            {
            }

            return "Unknown item";
        }
    }
}