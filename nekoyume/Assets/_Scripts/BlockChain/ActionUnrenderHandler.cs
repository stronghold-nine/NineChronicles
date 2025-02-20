using System;
using System.Collections.Generic;
using System.Linq;
using Lib9c.Renderer;
using Libplanet;
using Nekoyume.Action;
using Nekoyume.Model.Item;
using Nekoyume.Model.Mail;
using Nekoyume.Model.State;
using Nekoyume.State;
using Nekoyume.State.Subjects;
using Nekoyume.UI;
using UniRx;
using UnityEngine;

namespace Nekoyume.BlockChain
{
    using UniRx;

    public class ActionUnrenderHandler : ActionHandler
    {
        private static class Singleton
        {
            internal static readonly ActionUnrenderHandler Value = new ActionUnrenderHandler();
        }

        public static readonly ActionUnrenderHandler Instance = Singleton.Value;

        private ActionRenderer _renderer;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private ActionUnrenderHandler()
        {
        }

        public void Start(ActionRenderer renderer)
        {
            _renderer = renderer;

            RewardGold();
            // GameConfig(); todo.
            // CreateAvatar(); ignore.

            // Battle
            // HackAndSlash(); todo.
            // RankingBattle(); todo.
            // MimisbrunnrBattle(); todo.

            // Craft
            // CombinationConsumable(); todo.
            // CombinationEquipment(); todo.
            ItemEnhancement();
            // RapidCombination(); todo.

            // Market
            Sell();
            SellCancellation();
            Buy();

            // Consume
            DailyReward();
            // RedeemCode(); todo.
            // ChargeActionPoint(); todo.
            ClaimMonsterCollectionReward();
        }

        public void Stop()
        {
            _disposables.DisposeAllAndClear();
        }

        private void RewardGold()
        {
            _renderer.EveryUnrender<RewardGold>()
                .Where(HasUpdatedAssetsForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(onNext: eval =>
                {
                    // NOTE: 잘 들어오는지 확인하기 위해서 당분간 로그를 남깁니다.(2020.11.02)
                    try
                    {
                        var goldBalanceState = GetGoldBalanceState(eval);
                        Debug.Log($"Action unrender: {nameof(RewardGold)} | gold: {goldBalanceState.Gold}");
                    }
                    catch (Exception e)
                    {
                        Debug.Log($"Action unrender: {nameof(RewardGold)} | {e}");
                    }

                    UpdateAgentState(eval);
                })
                .AddTo(_disposables);
        }

        private void Buy()
        {
            _renderer.EveryUnrender<Buy>()
                .ObserveOnMainThread()
                .Subscribe(ResponseBuy)
                .AddTo(_disposables);
        }

        private void Sell()
        {
            _renderer.EveryUnrender<Sell>()
                .Where(ValidateEvaluationForCurrentAvatarState)
                .ObserveOnMainThread()
                .Subscribe(ResponseSell)
                .AddTo(_disposables);
        }

        private void SellCancellation()
        {
            _renderer.EveryUnrender<SellCancellation>()
                .Where(ValidateEvaluationForCurrentAvatarState)
                .ObserveOnMainThread()
                .Subscribe(ResponseSellCancellation)
                .AddTo(_disposables);
        }

        private void ItemEnhancement()
        {
            _renderer.EveryUnrender<ItemEnhancement>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseUnrenderItemEnhancement)
                .AddTo(_disposables);
        }

        private void DailyReward()
        {
            _renderer.EveryUnrender<DailyReward>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseDailyReward)
                .AddTo(_disposables);
        }

        private void ClaimMonsterCollectionReward()
        {
            _renderer.EveryUnrender<ClaimMonsterCollectionReward>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseClaimMonsterCollectionReward)
                .AddTo(_disposables);
        }

        private void ResponseBuy(ActionBase.ActionEvaluation<Buy> eval)
        {
            if (!(eval.Exception is null))
            {
                return;
            }

            var currentAvatarAddress = States.Instance.CurrentAvatarState.address;
            var currentAvatarState = eval.OutputStates.GetAvatarState(currentAvatarAddress);

            if (eval.Action.buyerAvatarAddress == currentAvatarAddress)
            {
                var agentAddress = States.Instance.AgentState.address;
                var purchaseResults = eval.Action.buyerMultipleResult.purchaseResults;
                foreach (var purchaseResult in purchaseResults)
                {
                    if (purchaseResult.errorCode == 0)
                    {
                        // Local layer
                        var price = purchaseResult.shopItem.Price;
                        var itemBase = ShopBuy.GetItemBase(purchaseResult);
                        var count = purchaseResult.tradableFungibleItemCount > 0
                            ? purchaseResult.tradableFungibleItemCount
                            : 1;
                        var tradableItem = (ITradableItem) itemBase;
                        LocalLayerModifier.ModifyAgentGold(agentAddress, -price);
                        LocalLayerModifier.AddItem(currentAvatarAddress, tradableItem.TradableId, tradableItem.RequiredBlockIndex, count);
                        LocalLayerModifier.RemoveNewAttachmentMail(currentAvatarAddress, purchaseResult.id);
                    }
                    else
                    {
                        if (!ReactiveShopState.PurchaseHistory.ContainsKey(eval.Action.Id))
                        {
                            Debug.LogError($"purchaseHistory is null : {eval.Action.Id}");
                            continue;
                        }

                        var purchaseHistory = ReactiveShopState.PurchaseHistory[eval.Action.Id];
                        var item = purchaseHistory.FirstOrDefault(x => x.ProductId.Value == purchaseResult.productId);
                        if (item is null)
                        {
                            continue;
                        }

                        // Local layer
                        var price = item.Price.Value;
                        LocalLayerModifier.ModifyAgentGold(agentAddress, -price);
                    }
                }
            }
            else
            {
                foreach (var sellerResult in eval.Action.sellerMultipleResult.sellerResults)
                {
                    if (sellerResult.shopItem.SellerAvatarAddress != currentAvatarAddress)
                    {
                        continue;
                    }

                    // Local layer
                    LocalLayerModifier.ModifyAgentGold(currentAvatarAddress, sellerResult.gold);
                    LocalLayerModifier.RemoveNewAttachmentMail(currentAvatarAddress, sellerResult.id);
                }
            }

            UpdateAgentState(eval);
            UpdateCurrentAvatarState(eval);
            UnrenderQuest(currentAvatarAddress, currentAvatarState.questList.completedQuestIds);
        }

        private void ResponseSell(ActionBase.ActionEvaluation<Sell> eval)
        {
            if (!(eval.Exception is null))
            {
                return;
            }

            var avatarAddress = eval.Action.sellerAvatarAddress;
            var itemId = eval.Action.tradableId;
            var blockIndex = Game.Game.instance.Agent.BlockIndex;
            var count = eval.Action.count;
            LocalLayerModifier.RemoveItem(avatarAddress, itemId, blockIndex, count);
            UpdateCurrentAvatarState(eval);
        }

        private void ResponseSellCancellation(ActionBase.ActionEvaluation<SellCancellation> eval)
        {
            if (!(eval.Exception is null))
            {
                return;
            }

            var avatarAddress = eval.Action.sellerAvatarAddress;
            var result = eval.Action.result;
            var itemBase = ShopSell.GetItemBase(result);
            var count = result.tradableFungibleItemCount > 0
                ? result.tradableFungibleItemCount
                : 1;
            var tradableItem = (ITradableItem) itemBase;

            LocalLayerModifier.AddItem(avatarAddress, tradableItem.TradableId, tradableItem.RequiredBlockIndex, count);
            UpdateCurrentAvatarState(eval);
        }

        private void ResponseDailyReward(ActionBase.ActionEvaluation<DailyReward> eval)
        {
            if (!(eval.Exception is null))
            {
                return;
            }

            var avatarAddress = eval.Action.avatarAddress;
            var fungibleId = eval.Action.dailyRewardResult.materials.First().Key.ItemId;
            var itemCount = eval.Action.dailyRewardResult.materials.First().Value;
            LocalLayerModifier.AddItem(avatarAddress, fungibleId, itemCount);
            var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);
            ReactiveAvatarState.DailyRewardReceivedIndex.SetValueAndForceNotify(
                avatarState.dailyRewardReceivedIndex);
            GameConfigStateSubject.IsChargingActionPoint.SetValueAndForceNotify(false);
            UpdateCurrentAvatarState(avatarState);
        }

        private void ResponseUnrenderItemEnhancement(ActionBase.ActionEvaluation<ItemEnhancement> eval)
        {
            var agentAddress = eval.Signer;
            var avatarAddress = eval.Action.avatarAddress;
            var slot = eval.OutputStates.GetCombinationSlotState(avatarAddress, eval.Action.slotIndex);
            var result = (ItemEnhancement.ResultModel)slot.Result;
            var itemUsable = result.itemUsable;
            var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);

            // NOTE: 사용한 자원에 대한 레이어 다시 추가하기.
            LocalLayerModifier.ModifyAgentGold(agentAddress, -result.gold);
            LocalLayerModifier.RemoveItem(avatarAddress, itemUsable.ItemId, itemUsable.RequiredBlockIndex, 1);
            foreach (var itemId in result.materialItemIdList)
            {
                if (avatarState.inventory.TryGetNonFungibleItem(itemId, out ItemUsable materialItem))
                {
                    LocalLayerModifier.RemoveItem(avatarAddress, itemId, materialItem.RequiredBlockIndex, 1);
                }
            }

            // NOTE: 메일 레이어 다시 없애기.
            LocalLayerModifier.AddItem(avatarAddress, itemUsable.TradableId, itemUsable.RequiredBlockIndex, 1);
            LocalLayerModifier.RemoveNewAttachmentMail(avatarAddress, result.id);

            // NOTE: 워크샵 슬롯의 모든 휘발성 상태 변경자를 다시 추가하기.
            var otherItemId = result.materialItemIdList.First();
            LocalLayerModifier.ModifyCombinationSlotItemEnhancement(
                itemUsable.ItemId,
                otherItemId,
                eval.Action.slotIndex);

            UpdateAgentState(eval);
            UpdateCurrentAvatarState(eval);
            UpdateCombinationSlotState(slot);
            UnrenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
        }

        private void ResponseClaimMonsterCollectionReward(ActionBase.ActionEvaluation<ClaimMonsterCollectionReward> eval)
        {
            if (!(eval.Exception is null))
            {
                return;
            }

            var avatarAddress = eval.Action.avatarAddress;
            var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);
            var mail = avatarState.mailBox.FirstOrDefault(e => e is MonsterCollectionMail);
            if (!(mail is MonsterCollectionMail {attachment: MonsterCollectionResult monsterCollectionResult}))
            {
                return;
            }

            // LocalLayer
            var rewardInfos = monsterCollectionResult.rewards;
            for (var i = 0; i < rewardInfos.Count; i++)
            {
                var rewardInfo = rewardInfos[i];
                if (!rewardInfo.ItemId.TryParseAsTradableId(
                    Game.Game.instance.TableSheets.ItemSheet,
                    out var tradableId))
                {
                    continue;
                }

                if (!rewardInfo.ItemId.TryGetFungibleId(
                    Game.Game.instance.TableSheets.ItemSheet,
                    out var fungibleId))
                {
                    continue;
                }

                avatarState.inventory.TryGetFungibleItems(fungibleId, out var items);
                var item = items.FirstOrDefault(x => x.item is ITradableItem);
                if (item != null && item is ITradableItem tradableItem)
                {
                    LocalLayerModifier.AddItem(avatarAddress, tradableId, tradableItem.RequiredBlockIndex, rewardInfo.Quantity);
                }
            }

            LocalLayerModifier.RemoveNewAttachmentMail(avatarAddress, mail.id);
            // ~LocalLayer

            UpdateAgentState(eval);
            UpdateCurrentAvatarState(eval);
            UnrenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
        }

        public static void UnrenderQuest(Address avatarAddress, IEnumerable<int> ids)
        {
            if (avatarAddress != States.Instance.CurrentAvatarState.address)
            {
                return;
            }

            foreach (var id in ids)
            {
                LocalLayerModifier.RemoveReceivableQuest(avatarAddress, id);
            }
        }
    }
}
