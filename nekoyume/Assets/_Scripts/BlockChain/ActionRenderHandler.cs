using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bencodex.Types;
using Lib9c.Renderer;
using Libplanet;
using Libplanet.Assets;
using Nekoyume.Action;
using Nekoyume.L10n;
using Nekoyume.Model.Mail;
using Nekoyume.Model.Item;
using Nekoyume.State;
using Nekoyume.UI;
using UniRx;
using Nekoyume.Model.State;
using TentuPlay.Api;
using Nekoyume.Model.Quest;
using Nekoyume.State.Modifiers;
using Nekoyume.State.Subjects;
using Nekoyume.UI.Module;
using UnityEngine;

namespace Nekoyume.BlockChain
{
    using UniRx;

    /// <summary>
    /// 현상태 : 각 액션의 랜더 단계에서 즉시 게임 정보에 반영시킴. 아바타를 선택하지 않은 상태에서 이전에 성공시키지 못한 액션을 재수행하고
    ///       이를 핸들링하면, 즉시 게임 정보에 반영시길 수 없기 때문에 에러가 발생함.
    /// 참고 : 이후 언랜더 처리를 고려한 해법이 필요함.
    /// 해법 1: 랜더 단계에서 얻는 `eval` 자체 혹은 변경점을 queue에 넣고, 게임의 상태에 따라 꺼내 쓰도록.
    /// </summary>
    public class ActionRenderHandler : ActionHandler
    {
        private static class Singleton
        {
            internal static readonly ActionRenderHandler Value = new ActionRenderHandler();
        }

        public static ActionRenderHandler Instance => Singleton.Value;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private ActionRenderer _renderer;

        private IDisposable _disposableForBattleEnd = null;

        private ActionRenderHandler()
        {
        }

        public void Start(ActionRenderer renderer)
        {
            _renderer = renderer;

            RewardGold();
            GameConfig();
            CreateAvatar();

            // Battle
            HackAndSlash();
            RankingBattle();
            MimisbrunnrBattle();

            // Craft
            CombinationConsumable();
            CombinationEquipment();
            ItemEnhancement();
            RapidCombination();

            // Market
            Sell();
            SellCancellation();
            Buy();

            // Consume
            DailyReward();
            RedeemCode();
            ChargeActionPoint();
            ClaimMonsterCollectionReward();
        }

        public void Stop()
        {
            _disposables.DisposeAllAndClear();
        }

        private void RewardGold()
        {
            // FIXME RewardGold의 결과(ActionEvaluation)에서 다른 갱신 주소가 같이 나오고 있는데 더 조사해봐야 합니다.
            // 우선은 HasUpdatedAssetsForCurrentAgent로 다르게 검사해서 우회합니다.
            _renderer.EveryRender<RewardGold>()
                .Where(HasUpdatedAssetsForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(eval =>
                {
                    //[TentuPlay] RewardGold 기록
                    //Local에서 변경하는 States.Instance 보다는 블락에서 꺼내온 eval.OutputStates를 사용
                    Address agentAddress = States.Instance.AgentState.address;
                    if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var balance))
                    {
                        new TPStashEvent().CharacterCurrencyGet(
                            player_uuid: agentAddress.ToHex(),
                            // FIXME: Sometimes `States.Instance.CurrentAvatarState` is null.
                            character_uuid: States.Instance.CurrentAvatarState?.address.ToHex().Substring(0, 4) ?? string.Empty,
                            currency_slug: "gold",
                            currency_quantity: float.Parse((balance - States.Instance.GoldBalanceState.Gold).GetQuantityString()),
                            currency_total_quantity: float.Parse(balance.GetQuantityString()),
                            reference_entity: entity.Bonuses,
                            reference_category_slug: "reward_gold",
                            reference_slug: "RewardGold");
                    }

                    UpdateAgentState(eval);

                }).AddTo(_disposables);
        }

        private void CreateAvatar()
        {
            _renderer.EveryRender<CreateAvatar2>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(eval =>
                {
                    //[TentuPlay] 캐릭터 획득
                    Address agentAddress = States.Instance.AgentState.address;
                    Address avatarAddress = agentAddress.Derive(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            CreateAvatar2.DeriveFormat,
                            eval.Action.index
                        )
                    );
                    new TPStashEvent().PlayerCharacterGet(
                        player_uuid: agentAddress.ToHex(),
                        character_uuid: avatarAddress.ToHex().Substring(0, 4),
                        characterarchetype_slug: Nekoyume.GameConfig.DefaultAvatarCharacterId.ToString(), //100010 for now.
                        //-> WARRIOR, ARCHER, MAGE, ACOLYTE를 구분할 수 있는 구분자여야한다.
                        reference_entity: entity.Etc,
                        reference_category_slug: null,
                        reference_slug: null
                    );

                    UpdateAgentState(eval);
                    UpdateAvatarState(eval, eval.Action.index);
                }).AddTo(_disposables);
        }

        private void HackAndSlash()
        {
            _renderer.EveryRender<HackAndSlash>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseHackAndSlash).AddTo(_disposables);

        }

        private void MimisbrunnrBattle()
        {
            _renderer.EveryRender<MimisbrunnrBattle>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseMimisbrunnr).AddTo(_disposables);
        }

        private void CombinationConsumable()
        {
            _renderer.EveryRender<CombinationConsumable>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseCombinationConsumable).AddTo(_disposables);
        }

        private void Sell()
        {
            _renderer.EveryRender<Sell>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseSell).AddTo(_disposables);
        }

        private void SellCancellation()
        {
            _renderer.EveryRender<SellCancellation>()
                .Where(ValidateEvaluationForCurrentAvatarState)
                .ObserveOnMainThread()
                .Subscribe(ResponseSellCancellation).AddTo(_disposables);
        }

        private void Buy()
        {
            _renderer.EveryRender<Buy>()
                .ObserveOnMainThread()
                .Subscribe(ResponseBuy).AddTo(_disposables);
        }

        private void ItemEnhancement()
        {
            _renderer.EveryRender<ItemEnhancement>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseItemEnhancement).AddTo(_disposables);
        }

        private void DailyReward()
        {
            _renderer.EveryRender<DailyReward>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(eval =>
                {
                    LocalLayer.Instance
                        .ClearAvatarModifiers<AvatarDailyRewardReceivedIndexModifier>(
                            eval.Action.avatarAddress);

                    UpdateCurrentAvatarState(eval);

                    if (eval.Exception is null)
                    {
                        UI.Notification.Push(
                            Nekoyume.Model.Mail.MailType.System,
                            L10nManager.Localize("UI_RECEIVED_DAILY_REWARD"));
                        var avatarAddress = eval.Action.avatarAddress;
                        var itemId = eval.Action.dailyRewardResult.materials.First().Key.ItemId;
                        var itemCount = eval.Action.dailyRewardResult.materials.First().Value;
                        LocalLayerModifier.RemoveItem(avatarAddress, itemId, itemCount);
                        LocalLayerModifier.AddNewAttachmentMail(avatarAddress, eval.Action.dailyRewardResult.id);
                        GameConfigStateSubject.IsChargingActionPoint.SetValueAndForceNotify(false);
                    }

                }).AddTo(_disposables);
        }

        private void RankingBattle()
        {
            _renderer.EveryRender<RankingBattle>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseRankingBattle).AddTo(_disposables);
        }

        private void CombinationEquipment()
        {
            _renderer.EveryRender<CombinationEquipment>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseCombinationEquipment).AddTo(_disposables);
        }

        private void RapidCombination()
        {
            _renderer.EveryRender<RapidCombination>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseRapidCombination).AddTo(_disposables);
        }

        private void GameConfig()
        {
            _renderer.EveryRender(GameConfigState.Address)
                .ObserveOnMainThread()
                .Subscribe(UpdateGameConfigState).AddTo(_disposables);
        }

        private void RedeemCode()
        {
            _renderer.EveryRender<Action.RedeemCode>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseRedeemCode).AddTo(_disposables);
        }

        private void ChargeActionPoint()
        {
            _renderer.EveryRender<ChargeActionPoint>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseChargeActionPoint).AddTo(_disposables);
        }

        private void ClaimMonsterCollectionReward()
        {
            _renderer.EveryRender<ClaimMonsterCollectionReward>()
                .Where(ValidateEvaluationForCurrentAgent)
                .ObserveOnMainThread()
                .Subscribe(ResponseClaimMonsterCollectionReward).AddTo(_disposables);
        }

        private void ResponseRapidCombination(ActionBase.ActionEvaluation<RapidCombination> eval)
        {
            if (eval.Exception is null)
            {
                var avatarAddress = eval.Action.avatarAddress;
                var slot =
                    eval.OutputStates.GetCombinationSlotState(avatarAddress, eval.Action.slotIndex);
                var result = (RapidCombination0.ResultModel) slot.Result;
                foreach (var pair in result.cost)
                {
                    // NOTE: 최종적으로 UpdateCurrentAvatarState()를 호출한다면, 그곳에서 상태를 새로 설정할 것이다.
                    LocalLayerModifier.AddItem(avatarAddress, pair.Key.ItemId, pair.Value);
                }
                LocalLayerModifier.RemoveAvatarItemRequiredIndex(avatarAddress, result.itemUsable.NonFungibleId);
                LocalLayerModifier.ResetCombinationSlot(slot);

                //[TentuPlay] RapidCombinationConsumable 합성에 사용한 골드 기록
                //Local에서 변경하는 States.Instance 보다는 블락에서 꺼내온 eval.OutputStates를 사용
                var agentAddress = eval.Signer;
                var qty = eval.OutputStates.GetAvatarState(avatarAddress).inventory.Materials
                    .Count(i => i.ItemSubType == ItemSubType.Hourglass);
                var prevQty = eval.PreviousStates.GetAvatarState(avatarAddress).inventory.Materials
                    .Count(i => i.ItemSubType == ItemSubType.Hourglass);
                new TPStashEvent().CharacterItemUse(
                    player_uuid: agentAddress.ToHex(),
                    character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                    item_category: itemCategory.Consumable,
                    item_slug: "hourglass",
                    item_quantity: (float)(prevQty - qty),
                    reference_entity: entity.Items,
                    reference_category_slug: "consumables_rapid_combination",
                    reference_slug: slot.Result.itemUsable.Id.ToString()
                );

                UpdateAgentState(eval);
                UpdateCurrentAvatarState(eval);
                UpdateCombinationSlotState(slot);
            }
        }

        private void ResponseCombinationEquipment(ActionBase.ActionEvaluation<CombinationEquipment> eval)
        {
            if (eval.Exception is null)
            {
                var agentAddress = eval.Signer;
                var avatarAddress = eval.Action.AvatarAddress;
                var slot = eval.OutputStates.GetCombinationSlotState(avatarAddress, eval.Action.SlotIndex);
                var result = (CombinationConsumable.ResultModel) slot.Result;
                var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);

                // NOTE: 사용한 자원에 대한 레이어 벗기기.
                LocalLayerModifier.ModifyAgentGold(agentAddress, result.gold);
                LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, result.actionPoint);
                foreach (var pair in result.materials)
                {
                    LocalLayerModifier.AddItem(avatarAddress, pair.Key.ItemId, pair.Value);
                }

                // NOTE: 메일 레이어 씌우기.
                LocalLayerModifier.RemoveItem(avatarAddress, result.itemUsable.ItemId, result.itemUsable.RequiredBlockIndex, 1);
                LocalLayerModifier.AddNewAttachmentMail(avatarAddress, result.id);
                LocalLayerModifier.ResetCombinationSlot(slot);

                // NOTE: 노티 예약 걸기.
                var format = L10nManager.Localize("NOTIFICATION_COMBINATION_COMPLETE");
                UI.Notification.Reserve(
                    MailType.Workshop,
                    string.Format(format, result.itemUsable.GetLocalizedName()),
                    slot.UnlockBlockIndex,
                    result.itemUsable.ItemId);

                //[TentuPlay] Equipment 합성에 사용한 골드 기록
                //Local에서 변경하는 States.Instance 보다는 블락에서 꺼내온 eval.OutputStates를 사용
                if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var balance))
                {
                    var total = balance - new FungibleAssetValue(balance.Currency, result.gold, 0);
                    new TPStashEvent().CharacterCurrencyUse(
                        player_uuid: agentAddress.ToHex(),
                        character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                        currency_slug: "gold",
                        currency_quantity: (float) result.gold,
                        currency_total_quantity: float.Parse(total.GetQuantityString()),
                        reference_entity: entity.Items,
                        reference_category_slug: "equipments_combination",
                        reference_slug: result.itemUsable.Id.ToString());
                }

                var gameInstance = Game.Game.instance;

                var nextQuest = gameInstance.States.CurrentAvatarState.questList?
                    .OfType<CombinationEquipmentQuest>()
                    .Where(x => !x.Complete)
                    .OrderBy(x => x.StageId)
                    .FirstOrDefault(x =>
                        gameInstance.TableSheets.EquipmentItemRecipeSheet.TryGetValue(x.RecipeId, out _));

                UpdateAgentState(eval);
                UpdateCurrentAvatarState(eval);
                RenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
                UpdateCombinationSlotState(slot);

                if (!(nextQuest is null))
                {
                    var isRecipeMatch = nextQuest.RecipeId == eval.Action.RecipeId;

                    if (isRecipeMatch)
                    {
                        var celebratesPopup = Widget.Find<CelebratesPopup>();
                        celebratesPopup.Show(nextQuest);
                        celebratesPopup.OnDisableObservable
                            .First()
                            .Subscribe(_ =>
                            {
                                var menu = Widget.Find<Menu>();
                                if (menu.isActiveAndEnabled)
                                {
                                    menu.UpdateGuideQuest(avatarState);
                                }

                                var combination = Widget.Find<Combination>();
                                if (combination.isActiveAndEnabled)
                                {
                                    combination.UpdateRecipe();
                                }
                            });
                    }
                }
            }
        }

        private void ResponseCombinationConsumable(ActionBase.ActionEvaluation<CombinationConsumable> eval)
        {
            if (eval.Exception is null)
            {
                var agentAddress = eval.Signer;
                var avatarAddress = eval.Action.AvatarAddress;
                var slot = eval.OutputStates.GetCombinationSlotState(avatarAddress, eval.Action.slotIndex);
                var result = (CombinationConsumable.ResultModel) slot.Result;
                var itemUsable = result.itemUsable;
                var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);

                LocalLayerModifier.ModifyAgentGold(agentAddress, result.gold);
                LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, result.actionPoint);
                foreach (var pair in result.materials)
                {
                    LocalLayerModifier.AddItem(avatarAddress, pair.Key.ItemId, pair.Value);
                }

                LocalLayerModifier.RemoveItem(avatarAddress, itemUsable.ItemId, itemUsable.RequiredBlockIndex, 1);
                LocalLayerModifier.AddNewAttachmentMail(avatarAddress, result.id);
                LocalLayerModifier.ResetCombinationSlot(slot);

                var format = L10nManager.Localize("NOTIFICATION_COMBINATION_COMPLETE");
                UI.Notification.Reserve(
                    MailType.Workshop,
                    string.Format(format, result.itemUsable.GetLocalizedName()),
                    slot.UnlockBlockIndex,
                    result.itemUsable.ItemId
                );

                if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var balance))
                {
                    var total = balance - new FungibleAssetValue(balance.Currency, result.gold, 0);
                    new TPStashEvent().CharacterCurrencyUse(
                        player_uuid: agentAddress.ToHex(),
                        character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                        currency_slug: "gold",
                        currency_quantity: (float)result.gold,
                        currency_total_quantity: float.Parse(total.GetQuantityString()),
                        reference_entity: entity.Items,
                        reference_category_slug: "consumables_combination",
                        reference_slug: result.itemUsable.Id.ToString());
                }

                UpdateAgentState(eval);
                UpdateCurrentAvatarState(eval);
                UpdateCombinationSlotState(slot);
                RenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
            }
        }

        private void ResponseSell(ActionBase.ActionEvaluation<Sell> eval)
        {
            if (eval.Exception is null)
            {
                var avatarAddress = eval.Action.sellerAvatarAddress;
                var tradableId = eval.Action.tradableId;
                var blockIndex = Game.Game.instance.Agent.BlockIndex;
                var count = eval.Action.count;
                var avatarState = new AvatarState((Bencodex.Types.Dictionary) eval.PreviousStates.GetState(avatarAddress));
                if (avatarState.inventory.TryGetTradableItems(tradableId, blockIndex, count, out var items))
                {
                    string message = string.Empty;
                    if (count > 1)
                    {
                        message = string.Format(L10nManager.Localize("NOTIFICATION_MULTIPLE_SELL_COMPLETE"),
                            items.First().item.GetLocalizedName(),
                            count);
                    }
                    else
                    {
                        message = string.Format(L10nManager.Localize("NOTIFICATION_SELL_COMPLETE"),
                            items.First().item.GetLocalizedName());
                    }

                    OneLinePopup.Push(MailType.Auction, message);
                }
                else
                {
                    Debug.LogError("Failed to get non-fungible item from previous AvatarState.");
                }

                UpdateCurrentAvatarState(eval);
            }
        }

        private void ResponseSellCancellation(ActionBase.ActionEvaluation<SellCancellation> eval)
        {
            if (eval.Exception is null)
            {
                var avatarAddress = eval.Action.sellerAvatarAddress;
                var result = eval.Action.result;
                var itemBase = ShopSell.GetItemBase(result);
                var count = result.tradableFungibleItemCount > 0
                    ? result.tradableFungibleItemCount
                    : 1;
                var tradableItem = (ITradableItem) itemBase;
                LocalLayerModifier.RemoveItem(avatarAddress, tradableItem.TradableId, tradableItem.RequiredBlockIndex, count);
                LocalLayerModifier.AddNewAttachmentMail(avatarAddress, result.id);
                var format = L10nManager.Localize("NOTIFICATION_SELL_CANCEL_COMPLETE");
                OneLinePopup.Push(MailType.Auction, string.Format(format, itemBase.GetLocalizedName()));
                UpdateCurrentAvatarState(eval);
            }
        }

        private void ResponseBuy(ActionBase.ActionEvaluation<Buy> eval)
        {
            if (eval.Exception is null)
            {
                var agentAddress = States.Instance.AgentState.address;
                var currentAvatarAddress = States.Instance.CurrentAvatarState.address;
                var currentAvatarState = eval.OutputStates.GetAvatarState(currentAvatarAddress);
                if (eval.Action.buyerAvatarAddress == currentAvatarAddress)
                {
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
                            LocalLayerModifier.ModifyAgentGold(agentAddress, price);
                            LocalLayerModifier.RemoveItem(currentAvatarAddress, tradableItem.TradableId, tradableItem.RequiredBlockIndex, count);
                            LocalLayerModifier.AddNewAttachmentMail(currentAvatarAddress, purchaseResult.id);

                            // Push notification
                            var format = L10nManager.Localize("NOTIFICATION_BUY_BUYER_COMPLETE");
                            OneLinePopup.Push(MailType.Auction, string.Format(format, itemBase.GetLocalizedName(), price));

                            // Analytics
                            if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var buyerAgentBalance))
                            {
                                var total = buyerAgentBalance - price;
                                new TPStashEvent().CharacterCurrencyUse(
                                    player_uuid: States.Instance.AgentState.address.ToHex(),
                                    character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                                    currency_slug: "gold",
                                    currency_quantity: float.Parse(price.GetQuantityString()),
                                    currency_total_quantity: float.Parse(total.GetQuantityString()),
                                    reference_entity: entity.Trades,
                                    reference_category_slug: "buy",
                                    reference_slug: itemBase.Id.ToString()
                                );
                            }
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
                            LocalLayerModifier.ModifyAgentGold(agentAddress, price);

                            // Push notification
                            var errorType = ((ShopErrorType) purchaseResult.errorCode).ToString();
                            var msg = string.Format(L10nManager.Localize("NOTIFICATION_BUY_FAIL"),
                                item.ItemBase.Value.GetLocalizedName(),
                                L10nManager.Localize(errorType),
                                price);
                            OneLinePopup.Push(MailType.Auction, msg);
                        }
                    }
                }
                else
                {
                    var buyerAvatarAddress = eval.Action.buyerAvatarAddress;
                    var buyerAvatarStateValue = eval.OutputStates.GetState(buyerAvatarAddress);
                    if (buyerAvatarStateValue is null)
                    {
                        Debug.LogError("buyerAvatarStateValue is null.");
                        return;
                    }

                    // Make buyer name with hash
                    // Reference AvatarState.PostConstructor()
                    const string nameWithHashFormat = "{0} <size=80%><color=#A68F7E>#{1}</color></size>";
                    var buyerNameWithHash = string.Format(
                        nameWithHashFormat,
                        ((Text) ((Dictionary) buyerAvatarStateValue)["name"]).Value,
                        buyerAvatarAddress.ToHex().Substring(0, 4)
                    );

                    foreach (var sellerResult in eval.Action.sellerMultipleResult.sellerResults)
                    {
                        if (sellerResult.shopItem.SellerAvatarAddress != currentAvatarAddress)
                        {
                            continue;
                        }

                        // Local layer
                        LocalLayerModifier.ModifyAgentGold(agentAddress, -sellerResult.gold);
                        LocalLayerModifier.AddNewAttachmentMail(currentAvatarAddress, sellerResult.id);

                        // Push notification
                        var itemBase = sellerResult.itemUsable ?? (ItemBase) sellerResult.costume;
                        var message = string.Format(
                            L10nManager.Localize("NOTIFICATION_BUY_SELLER_COMPLETE"),
                            buyerNameWithHash,
                            itemBase.GetLocalizedName());
                        OneLinePopup.Push(MailType.Auction, message);

                        // Analytics
                        if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency,
                            out var sellerAgentBalance))
                        {
                            var price = sellerResult.shopItem.Price;
                            var total = sellerAgentBalance + price;
                            new TPStashEvent().CharacterCurrencyGet(
                                player_uuid: agentAddress.ToHex(), // seller == 본인인지 확인필요
                                character_uuid: States.Instance.CurrentAvatarState.address.ToHex()
                                    .Substring(0, 4),
                                currency_slug: "gold",
                                currency_quantity: float.Parse(price.GetQuantityString()),
                                currency_total_quantity: float.Parse(total.GetQuantityString()),
                                reference_entity: entity.Trades,
                                reference_category_slug: "sell",
                                reference_slug: itemBase.Id.ToString() //아이템 품번
                            );
                        }
                    }
                }

                UpdateAgentState(eval);
                UpdateCurrentAvatarState(eval);
                RenderQuest(currentAvatarAddress, currentAvatarState.questList.completedQuestIds);
            }
            else
            {
                Debug.Log(eval.Exception);
            }
        }

        private void ResponseHackAndSlash(ActionBase.ActionEvaluation<HackAndSlash> eval)
        {
            if (eval.Exception is null)
            {
                _disposableForBattleEnd?.Dispose();
                _disposableForBattleEnd =
                    Game.Game.instance.Stage.onEnterToStageEnd
                        .First()
                        .Subscribe(_ =>
                        {
                            UpdateCurrentAvatarState(eval);
                            UpdateWeeklyArenaState(eval);
                            var avatarState =
                                eval.OutputStates.GetAvatarState(eval.Action.avatarAddress);
                            RenderQuest(eval.Action.avatarAddress,
                                avatarState.questList.completedQuestIds);
                            _disposableForBattleEnd = null;
                        });

                if (Widget.Find<LoadingScreen>().IsActive())
                {
                    if (Widget.Find<QuestPreparation>().IsActive())
                    {
                        Widget.Find<QuestPreparation>().GoToStage(eval.Action.Result);
                    }
                    else if (Widget.Find<Menu>().IsActive())
                    {
                        Widget.Find<Menu>().GoToStage(eval.Action.Result);
                    }
                }
                else if (Widget.Find<StageLoadingScreen>().IsActive() &&
                         Widget.Find<BattleResult>().IsActive())
                {
                    Widget.Find<BattleResult>().NextStage(eval);
                }
            }
            else
            {
                var showLoadingScreen = false;
                if (Widget.Find<StageLoadingScreen>().IsActive())
                {
                    Widget.Find<StageLoadingScreen>().Close();
                }
                if (Widget.Find<BattleResult>().IsActive())
                {
                    showLoadingScreen = true;
                    Widget.Find<BattleResult>().Close();
                }

                var exc = eval.Exception.InnerException;
                BackToMain(showLoadingScreen, exc);
            }
        }

        private void ResponseMimisbrunnr(ActionBase.ActionEvaluation<MimisbrunnrBattle> eval)
        {
            if (eval.Exception is null)
            {
                _disposableForBattleEnd?.Dispose();
                _disposableForBattleEnd =
                    Game.Game.instance.Stage.onEnterToStageEnd
                        .First()
                        .Subscribe(_ =>
                        {
                            UpdateCurrentAvatarState(eval);
                            UpdateWeeklyArenaState(eval);
                            var avatarState =
                                eval.OutputStates.GetAvatarState(eval.Action.avatarAddress);
                            RenderQuest(eval.Action.avatarAddress,
                                avatarState.questList.completedQuestIds);
                            _disposableForBattleEnd = null;
                        });

                if (Widget.Find<LoadingScreen>().IsActive())
                {
                    if (Widget.Find<MimisbrunnrPreparation>().IsActive())
                    {
                        Widget.Find<MimisbrunnrPreparation>().GoToStage(eval.Action.Result);
                    }
                    else if (Widget.Find<Menu>().IsActive())
                    {
                        Widget.Find<Menu>().GoToStage(eval.Action.Result);
                    }
                }
                else if (Widget.Find<StageLoadingScreen>().IsActive() &&
                         Widget.Find<BattleResult>().IsActive())
                {
                    Widget.Find<BattleResult>().NextMimisbrunnrStage(eval);
                }
            }
            else
            {
                var showLoadingScreen = false;
                if (Widget.Find<StageLoadingScreen>().IsActive())
                {
                    Widget.Find<StageLoadingScreen>().Close();
                }
                if (Widget.Find<BattleResult>().IsActive())
                {
                    showLoadingScreen = true;
                    Widget.Find<BattleResult>().Close();
                }

                var exc = eval.Exception.InnerException;
                BackToMain(showLoadingScreen, exc);
            }
        }

        private void ResponseRankingBattle(ActionBase.ActionEvaluation<RankingBattle> eval)
        {
            if (eval.Exception is null)
            {
                var weeklyArenaAddress = eval.Action.WeeklyArenaAddress;
                var avatarAddress = eval.Action.AvatarAddress;

                LocalLayerModifier.RemoveWeeklyArenaInfoActivator(weeklyArenaAddress, avatarAddress);

                //[TentuPlay] RankingBattle 참가비 사용 기록 // 위의 fixme 내용과 어떻게 연결되는지?
                //Local에서 변경하는 States.Instance 보다는 블락에서 꺼내온 eval.OutputStates를 사용
                Address agentAddress = States.Instance.AgentState.address;
                if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var balance))
                {
                    var total = balance - new FungibleAssetValue(balance.Currency,
                        Nekoyume.GameConfig.ArenaActivationCostNCG, 0);
                    new TPStashEvent().CharacterCurrencyUse(
                        player_uuid: agentAddress.ToHex(),
                        character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                        currency_slug: "gold",
                        currency_quantity: (float)Nekoyume.GameConfig.ArenaActivationCostNCG,
                        currency_total_quantity: float.Parse(total.GetQuantityString()),
                        reference_entity: entity.Quests,
                        reference_category_slug: "arena",
                        reference_slug: "WeeklyArenaEntryFee"
                    );
                }

                _disposableForBattleEnd?.Dispose();
                _disposableForBattleEnd =
                    Game.Game.instance.Stage.onEnterToStageEnd
                        .First()
                        .Subscribe(_ =>
                        {
                            UpdateAgentState(eval);
                            UpdateCurrentAvatarState(eval);
                            UpdateWeeklyArenaState(eval);
                            _disposableForBattleEnd = null;
                        });

                if (Widget.Find<ArenaBattleLoadingScreen>().IsActive())
                {
                    Widget.Find<RankingBoard>().GoToStage(eval);
                }
            }
            else
            {
                var showLoadingScreen = false;
                if (Widget.Find<ArenaBattleLoadingScreen>().IsActive())
                {
                    Widget.Find<ArenaBattleLoadingScreen>().Close();
                }
                if (Widget.Find<RankingBattleResult>().IsActive())
                {
                    showLoadingScreen = true;
                    Widget.Find<RankingBattleResult>().Close();
                }

                BackToMain(showLoadingScreen, eval.Exception.InnerException);
            }
        }

        private void ResponseItemEnhancement(ActionBase.ActionEvaluation<ItemEnhancement> eval)
        {
            if (eval.Exception is null)
            {
                var agentAddress = eval.Signer;
                var avatarAddress = eval.Action.avatarAddress;
                var slot = eval.OutputStates.GetCombinationSlotState(avatarAddress, eval.Action.slotIndex);
                var result = (ItemEnhancement.ResultModel) slot.Result;
                var itemUsable = result.itemUsable;
                var avatarState = eval.OutputStates.GetAvatarState(avatarAddress);

                // NOTE: 사용한 자원에 대한 레이어 벗기기.
                LocalLayerModifier.ModifyAgentGold(agentAddress, result.gold);
                LocalLayerModifier.AddItem(avatarAddress, itemUsable.TradableId, itemUsable.RequiredBlockIndex, 1);
                foreach (var tradableId in result.materialItemIdList)
                {
                    if (avatarState.inventory.TryGetNonFungibleItem(tradableId,
                        out ItemUsable materialItem))
                    {
                        LocalLayerModifier.AddItem(avatarAddress, tradableId, materialItem.RequiredBlockIndex, 1);
                    }
                }

                // NOTE: 메일 레이어 씌우기.
                LocalLayerModifier.RemoveItem(avatarAddress, itemUsable.TradableId, itemUsable.RequiredBlockIndex, 1);
                LocalLayerModifier.AddNewAttachmentMail(avatarAddress, result.id);

                // NOTE: 워크샵 슬롯의 모든 휘발성 상태 변경자를 제거하기.
                LocalLayerModifier.ResetCombinationSlot(slot);

                // NOTE: 노티 예약 걸기.
                var format = L10nManager.Localize("NOTIFICATION_ITEM_ENHANCEMENT_COMPLETE");
                UI.Notification.Reserve(
                    MailType.Workshop,
                    string.Format(format, result.itemUsable.GetLocalizedName()),
                    slot.UnlockBlockIndex,
                    result.itemUsable.TradableId);

                //[TentuPlay] 장비강화, 골드사용
                //Local에서 변경하는 States.Instance 보다는 블락에서 꺼내온 eval.OutputStates를 사용
                if (eval.OutputStates.TryGetGoldBalance(agentAddress, GoldCurrency, out var outAgentBalance))
                {
                    var total = outAgentBalance -
                                new FungibleAssetValue(outAgentBalance.Currency, result.gold, 0);
                    new TPStashEvent().CharacterCurrencyUse(
                        player_uuid: agentAddress.ToHex(),
                        character_uuid: States.Instance.CurrentAvatarState.address.ToHex().Substring(0, 4),
                        currency_slug: "gold",
                        currency_quantity: (float) result.gold,
                        currency_total_quantity: float.Parse(total.GetQuantityString()),
                        reference_entity: entity.Items, //강화가 가능하므로 장비
                        reference_category_slug: "item_enhancement",
                        reference_slug: itemUsable.Id.ToString());
                }

                UpdateAgentState(eval);
                UpdateCurrentAvatarState(eval);
                UpdateCombinationSlotState(slot);
                RenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
            }
        }

        private void ResponseRedeemCode(ActionBase.ActionEvaluation<Action.RedeemCode> eval)
        {
            var key = "UI_REDEEM_CODE_INVALID_CODE";
            if (eval.Exception is null)
            {
                Widget.Find<CodeReward>().Show(eval.OutputStates.GetRedeemCodeState());
                key = "UI_REDEEM_CODE_SUCCESS";
                UpdateCurrentAvatarState(eval);
            }
            else
            {
                if (eval.Exception.InnerException is DuplicateRedeemException)
                {
                    key = "UI_REDEEM_CODE_ALREADY_USE";
                }
            }

            var msg = L10nManager.Localize(key);
            UI.Notification.Push(MailType.System, msg);
        }

        private void ResponseChargeActionPoint(ActionBase.ActionEvaluation<ChargeActionPoint> eval)
        {
            if (eval.Exception is null)
            {
                var avatarAddress = eval.Action.avatarAddress;
                LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, -States.Instance.GameConfigState.ActionPointMax);
                var row = Game.Game.instance.TableSheets.MaterialItemSheet.Values.First(r =>
                    r.ItemSubType == ItemSubType.ApStone);
                LocalLayerModifier.AddItem(avatarAddress, row.ItemId, 1);
                UpdateCurrentAvatarState(eval);
            }
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
                    LocalLayerModifier.RemoveItem(avatarAddress,
                                                  tradableId,
                                                  tradableItem.RequiredBlockIndex,
                                                  rewardInfo.Quantity);
                }
            }

            LocalLayerModifier.AddNewAttachmentMail(avatarAddress, mail.id);
            // ~LocalLayer

            // Notification
            UI.Notification.Push(
                MailType.System,
                L10nManager.Localize("NOTIFICATION_CLAIM_MONSTER_COLLECTION_REWARD_COMPLETE"));

            UpdateAgentState(eval);
            UpdateCurrentAvatarState(eval);
            RenderQuest(avatarAddress, avatarState.questList.completedQuestIds);
        }

        public static void RenderQuest(Address avatarAddress, IEnumerable<int> ids)
        {
            if (avatarAddress != States.Instance.CurrentAvatarState.address)
            {
                return;
            }

            var questList = States.Instance.CurrentAvatarState.questList;
            foreach (var id in ids)
            {
                var quest = questList.First(q => q.Id == id);
                var rewardMap = quest.Reward.ItemMap;

                foreach (var reward in rewardMap)
                {
                    var materialRow = Game.Game.instance.TableSheets
                        .MaterialItemSheet
                        .First(pair => pair.Key == reward.Key);

                    LocalLayerModifier.RemoveItem(
                        avatarAddress,
                        materialRow.Value.ItemId,
                        reward.Value);
                }

                LocalLayerModifier.AddReceivableQuest(avatarAddress, id);
            }
        }

        public static void BackToMain(bool showLoadingScreen, Exception exc)
        {
            Debug.LogException(exc);

            if (DoNotUsePopupError(exc, out var key, out var code, out var errorMsg))
            {
                return;
            }

            Game.Event.OnRoomEnter.Invoke(showLoadingScreen);
            Game.Game.instance.Stage.OnRoomEnterEnd
                .First()
                .Subscribe(_ => PopupError(key, code, errorMsg));

            MainCanvas.instance.InitWidgetInMain();
        }

        public static void PopupError(Exception exc)
        {
            Debug.LogException(exc);

            if (DoNotUsePopupError(exc, out var key, out var code, out var errorMsg))
            {
                return;
            }

            PopupError(key, code, errorMsg);
        }

        private static bool DoNotUsePopupError(Exception exc, out string key, out string code, out string errorMsg)
        {
            var tuple = ErrorCode.GetErrorCode(exc);
            key = tuple.Item1;
            code = tuple.Item2;
            errorMsg = tuple.Item3;
            if (code == "27")
            {
                // NOTE: `ActionTimeoutException` 이지만 아직 해당 액션이 스테이지 되어 있을 경우(27)에는 무시합니다.
                // 이 경우 `Game.Game.Instance.Agent`에서 블록 싱크를 시도하며 결과적으로 싱크에 성공하거나 `Disconnected`가 됩니다.
                // 싱크에 성공할 경우에는 `UnableToRenderWhenSyncingBlocksException` 예외로 다시 들어옵니다.
                // `Disconnected`가 될 경우에는 이 `BackToMain`이 호출되지 않고 `Game.Game.Instance.QuitWithAgentConnectionError()`가 호출됩니다.
                return true;
            }

            return false;
        }

        private static void PopupError(string key, string code, string errorMsg)
        {
            errorMsg = errorMsg == string.Empty
                ? string.Format(
                    L10nManager.Localize("UI_ERROR_RETRY_FORMAT"),
                    L10nManager.Localize(key),
                    code)
                : errorMsg;
            Widget
                .Find<SystemPopup>()
                .Show(L10nManager.Localize("UI_ERROR"), errorMsg,
                    L10nManager.Localize("UI_OK"), false);
        }
    }
}
