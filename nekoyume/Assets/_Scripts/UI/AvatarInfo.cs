using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nekoyume.Battle;
using Nekoyume.Game.Character;
using Nekoyume.Game.Controller;
using Nekoyume.Game.Factory;
using Nekoyume.Helper;
using Nekoyume.L10n;
using Nekoyume.Model.Item;
using Nekoyume.Model.Stat;
using Nekoyume.Model.State;
using Nekoyume.State;
using Nekoyume.UI.Model;
using Nekoyume.UI.Module;
using Nekoyume.UI.Tween;
using TMPro;
using UniRx;
using UnityEngine;

namespace Nekoyume.UI
{
    using UniRx;

    public class AvatarInfo : XTweenWidget
    {
        public bool HasNotification =>
            inventory.SharedModel.Equipments.Any(item => item.HasNotification.Value);

        private const string NicknameTextFormat = "<color=#B38271>Lv.{0}</color=> {1}";

        [SerializeField]
        private Module.Inventory inventory = null;

        [SerializeField]
        private TextMeshProUGUI nicknameText = null;

        [SerializeField]
        private Transform titleSocket = null;

        [SerializeField]
        private TextMeshProUGUI cpText = null;

        [SerializeField]
        private DigitTextTweener cpTextValueTweener = null;

        [SerializeField]
        private GameObject additionalCpArea = null;

        [SerializeField]
        private TextMeshProUGUI additionalCpText = null;

        [SerializeField]
        private EquipmentSlots costumeSlots = null;

        [SerializeField]
        private EquipmentSlots equipmentSlots = null;

        [SerializeField]
        private AvatarStats avatarStats = null;

        [SerializeField]
        private RectTransform avatarPosition = null;

        private EquipmentSlot _weaponSlot;
        private EquipmentSlot _armorSlot;
        private Player _player;
        private Vector3 _previousAvatarPosition;
        private int _previousSortingLayerID;
        private int _previousSortingLayerOrder;
        private bool _previousActivated;
        private Coroutine _disableCpTween;
        private GameObject _cachedCharacterTitle;

        public readonly ReactiveProperty<bool> IsTweenEnd = new ReactiveProperty<bool>(true);

        #region Override

        public override void Initialize()
        {
            base.Initialize();

            if (!equipmentSlots.TryGetSlot(ItemSubType.Weapon, out _weaponSlot))
            {
                throw new Exception($"Not found {ItemSubType.Weapon} slot in {equipmentSlots}");
            }

            if (!equipmentSlots.TryGetSlot(ItemSubType.Armor, out _armorSlot))
            {
                throw new Exception($"Not found {ItemSubType.Armor} slot in {equipmentSlots}");
            }

            inventory.SharedModel.State
                .Subscribe(inventoryState =>
                {
                    switch (inventoryState)
                    {
                        case ItemType.Consumable:
                            break;
                        case ItemType.Costume:
                            costumeSlots.gameObject.SetActive(true);
                            equipmentSlots.gameObject.SetActive(false);
                            break;
                        case ItemType.Equipment:
                            costumeSlots.gameObject.SetActive(false);
                            equipmentSlots.gameObject.SetActive(true);
                            break;
                        case ItemType.Material:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(inventoryState),
                                inventoryState, null);
                    }
                })
                .AddTo(gameObject);
            inventory.SharedModel.SelectedItemView
                .Subscribe(ShowTooltip)
                .AddTo(gameObject);
            inventory.OnDoubleClickItemView
                .Subscribe(itemView =>
                {
                    if (itemView is null ||
                        itemView.Model is null ||
                        itemView.Model.Dimmed.Value)
                    {
                        return;
                    }

                    Equip(itemView.Model);
                })
                .AddTo(gameObject);
            inventory.OnResetItems.Subscribe(SubscribeInventoryResetItems).AddTo(gameObject);

            foreach (var slot in equipmentSlots)
            {
                slot.ShowUnlockTooltip = true;
            }

            foreach (var slot in costumeSlots)
            {
                slot.ShowUnlockTooltip = true;
            }
        }

        public override void Show(bool ignoreShowAnimation = false)
        {
            Destroy(_cachedCharacterTitle);
            var currentAvatarState = Game.Game.instance.States.CurrentAvatarState;
            IsTweenEnd.Value = false;
            Show(currentAvatarState, ignoreShowAnimation);
        }

        protected override void OnTweenComplete()
        {
            base.OnTweenComplete();
            IsTweenEnd.Value = true;
        }

        protected override void OnTweenReverseComplete()
        {
            Game.Game.instance.Stage.objectPool.Remove<Player>(_player.gameObject);
            IsTweenEnd.Value = true;
        }

        public override void Close(bool ignoreCloseAnimation = false)
        {
            base.Close(ignoreCloseAnimation);
            IsTweenEnd.Value = false;
        }

        #endregion

        public void Show(AvatarState avatarState, bool ignoreShowAnimation = false)
        {
            base.Show(ignoreShowAnimation);
            inventory.SharedModel.State.Value = ItemType.Equipment;

            if (_player == null)
            {
                CreatePlayer(avatarState);
            }

            UpdateSlotView(avatarState);
            UpdateStatViews();
        }

        private void CreatePlayer(AvatarState avatarState)
        {
            var orderInLayer = MainCanvas.instance.GetLayer(WidgetType).root.sortingOrder + 1;
            _player = PlayerFactory.CreateBySettingLayer(avatarState, SortingLayer.NameToID("UI"), orderInLayer)
                                   .GetComponent<Player>();
            _player.Set(avatarState);
            _player.transform.SetParent(avatarPosition);
            _player.transform.localPosition = Vector3.zero;
        }

        private void UpdateUIPlayer()
        {
            var currentAvatarState = Game.Game.instance.States.CurrentAvatarState;
            _player.Set(currentAvatarState);
        }

        private void UpdateSlotView(AvatarState avatarState)
        {
            var game = Game.Game.instance;
            // var playerModel = game.Stage.GetPlayer().Model;
            var playerModel = _player.Model;

            nicknameText.text = string.Format(
                NicknameTextFormat,
                avatarState.level,
                avatarState.NameWithHash);

            var title = avatarState.inventory.Costumes.FirstOrDefault(costume =>
                costume.ItemSubType == ItemSubType.Title &&
                costume.equipped);

            if (!(title is null))
            {
                Destroy(_cachedCharacterTitle);
                var clone  = ResourcesHelper.GetCharacterTitle(title.Grade, title.GetLocalizedNonColoredName());
                _cachedCharacterTitle = Instantiate(clone, titleSocket);
            }

            costumeSlots.SetPlayerCostumes(playerModel, ShowTooltip, Unequip);
            equipmentSlots.SetPlayerEquipments(playerModel, ShowTooltip, Unequip);

            var currentAvatarState = game.States.CurrentAvatarState;
            if (avatarState.Equals(currentAvatarState))
            {
                // 인벤토리 아이템의 장착 여부를 `equipmentSlots`의 상태를 바탕으로 설정하기 때문에 `equipmentSlots.SetPlayer()`를 호출한 이후에 인벤토리 아이템의 장착 상태를 재설정한다.
                // 또한 인벤토리는 기본적으로 `OnEnable()` 단계에서 `OnResetItems` 이벤트를 일으키기 때문에 `equipmentSlots.SetPlayer()`와 호출 순서 커플링이 생기게 된다.
                // 따라서 강제로 상태를 설정한다.
                inventory.gameObject.SetActive(true);
                SubscribeInventoryResetItems(inventory);

                var currentPlayer = game.Stage.selectedPlayer;
                cpText.text = CPHelper.GetCP(currentPlayer.Model, game.TableSheets.CostumeStatSheet)
                    .ToString();
            }
            else
            {
                inventory.gameObject.SetActive(false);
                cpText.text = CPHelper.GetCPV2(avatarState, game.TableSheets.CharacterSheet, game.TableSheets.CostumeStatSheet)
                    .ToString();
            }

            UpdateUIPlayer();
        }

        private void UpdateStatViews()
        {
            var equipments = equipmentSlots
                .Where(slot => !slot.IsLock && !slot.IsEmpty)
                .Select(slot => slot.Item as Equipment)
                .Where(item => !(item is null))
                .ToList();

            var costumeIds = costumeSlots
                .Where(slot => !slot.IsLock && !slot.IsEmpty)
                .Select(slot => slot.Item.Id)
                .ToList();

            var costumeStatSheet = Game.Game.instance.TableSheets.CostumeStatSheet;
            var statModifiers = new List<StatModifier>();
            foreach (var itemId in costumeIds)
            {
                statModifiers.AddRange(
                    costumeStatSheet.OrderedList
                        .Where(r => r.CostumeId == itemId)
                        .Select(row => new StatModifier(row.StatType, StatModifier.OperationType.Add, (int) row.Stat))
                );
            }

            var currentAvatarState = Game.Game.instance.States.CurrentAvatarState;
            _player.Set(currentAvatarState);

            var stats = _player.Model.Stats.SetAll(
                _player.Model.Stats.Level,
                equipments,
                null,
                Game.Game.instance.TableSheets.EquipmentItemSetEffectSheet
            );
            stats.SetOption(statModifiers);
            avatarStats.SetData(stats);
            UpdateUIPlayer();
        }

        #region Subscribe

        private void SubscribeInventoryResetItems(Module.Inventory value)
        {
            inventory.SharedModel.EquippedEnabledFunc.SetValueAndForceNotify(inventoryItem =>
                TryToFindSlotAlreadyEquip(inventoryItem.ItemBase.Value, out _));
            inventory.SharedModel.UpdateEquipmentNotification();
        }

        #endregion

        private void Equip(CountableItem countableItem)
        {
            if (Game.Game.instance.Stage.IsInStage ||
                !(countableItem is InventoryItem inventoryItem))
            {
                return;
            }

            var itemBase = inventoryItem.ItemBase.Value;
            // 이미 장착중인 아이템이라면 해제한다.
            if (TryToFindSlotAlreadyEquip(itemBase, out var slot))
            {
                Unequip(slot);
                return;
            }

            // 아이템을 장착할 슬롯을 찾는다.
            if (!TryToFindSlotToEquip(itemBase, out slot))
            {
                return;
            }

            var currentAvatarState = Game.Game.instance.States.CurrentAvatarState;
            var characterSheet = Game.Game.instance.TableSheets.CharacterSheet;
            var costumeStatSheet = Game.Game.instance.TableSheets.CostumeStatSheet;
            var prevCp = CPHelper.GetCPV2(currentAvatarState, characterSheet, costumeStatSheet);

            // 이미 슬롯에 아이템이 있다면 해제한다.
            if (!slot.IsEmpty)
            {
                Unequip(slot, true);
            }

            slot.Set(itemBase, ShowTooltip, Unequip);
            LocalStateItemEquipModify(slot.Item, true);

            if (!(_disableCpTween is null))
                StopCoroutine(_disableCpTween);
            additionalCpArea.gameObject.SetActive(false);

            var currentCp = CPHelper.GetCPV2(currentAvatarState, characterSheet, costumeStatSheet);
            var tweener = cpTextValueTweener.Play(prevCp, currentCp);
            if (prevCp < currentCp)
            {
                additionalCpArea.gameObject.SetActive(true);
                additionalCpText.text = (currentCp - prevCp).ToString();
                _disableCpTween = StartCoroutine(CoDisableIncreasedCP());
            }

            var player = Game.Game.instance.Stage.GetPlayer();
            switch (itemBase)
            {
                default:
                    return;
                case Costume costume:
                {
                    inventoryItem.EquippedEnabled.Value = true;
                    player.EquipCostume(costume);
                    UpdateStatViews();
                    if (costume.ItemSubType == ItemSubType.Title)
                    {
                        Destroy(_cachedCharacterTitle);
                        var clone = ResourcesHelper.GetCharacterTitle(costume.Grade, costume.GetLocalizedNonColoredName());
                        _cachedCharacterTitle = Instantiate(clone, titleSocket);
                    }

                    break;
                }
                case Equipment _:
                {
                    inventoryItem.EquippedEnabled.Value = true;
                    UpdateStatViews();
                    switch (slot.ItemSubType)
                    {
                        case ItemSubType.Armor:
                        {
                            var armor = (Armor) _armorSlot.Item;
                            var weapon = (Weapon) _weaponSlot.Item;
                            player.EquipEquipmentsAndUpdateCustomize(armor, weapon);
                            break;
                        }
                        case ItemSubType.Weapon:
                            player.EquipWeapon((Weapon) slot.Item);
                            break;
                    }

                    break;
                }
            }

            Game.Event.OnUpdatePlayerEquip.OnNext(player);
            PostEquipOrUnequip(slot);
        }

        private IEnumerator CoDisableIncreasedCP()
        {
            yield return new WaitForSeconds(1.5f);
            additionalCpArea.gameObject.SetActive(false);
        }

        private void Unequip(EquipmentSlot slot)
        {
            Unequip(slot, false);
        }

        private void Unequip(EquipmentSlot slot, bool considerInventoryOnly)
        {
            if (Game.Game.instance.Stage.IsInStage)
            {
                return;
            }

            Find<ItemInformationTooltip>().Close();

            if (slot.IsEmpty)
            {
                foreach (var item in inventory.SharedModel.Equipments)
                {
                    item.GlowEnabled.Value =
                        item.ItemBase.Value.ItemSubType == slot.ItemSubType;
                }

                return;
            }

            var currentAvatarState = Game.Game.instance.States.CurrentAvatarState;
            var characterSheet = Game.Game.instance.TableSheets.CharacterSheet;
            var costumeStatSheet = Game.Game.instance.TableSheets.CostumeStatSheet;
            var prevCp = CPHelper.GetCPV2(currentAvatarState, characterSheet, costumeStatSheet);

            var slotItem = slot.Item;
            slot.Clear();
            LocalStateItemEquipModify(slotItem, false);

            var currentCp = CPHelper.GetCPV2(currentAvatarState, characterSheet, costumeStatSheet);
            cpTextValueTweener.Play(prevCp, currentCp);

            var player = considerInventoryOnly
                ? null
                : Game.Game.instance.Stage.GetPlayer();
            switch (slotItem)
            {
                default:
                    return;
                case Costume costume:
                {
                    if (!inventory.SharedModel.TryGetCostume(costume, out var inventoryItem))
                    {
                        return;
                    }

                    inventoryItem.EquippedEnabled.Value = false;

                    if (considerInventoryOnly)
                    {
                        break;
                    }

                    UpdateStatViews();

                    var armor = (Armor) _armorSlot.Item;
                    var weapon = (Weapon) _weaponSlot.Item;
                    player.UnequipCostume(costume, true);
                    player.EquipEquipmentsAndUpdateCustomize(armor, weapon);
                    Game.Event.OnUpdatePlayerEquip.OnNext(player);

                    if (costume.ItemSubType == ItemSubType.Title)
                    {
                        Destroy(_cachedCharacterTitle);
                    }

                    break;
                }
                case Equipment equipment:
                {
                    if (!inventory.SharedModel.TryGetEquipment(equipment, out var inventoryItem))
                    {
                        return;
                    }

                    inventoryItem.EquippedEnabled.Value = false;

                    if (considerInventoryOnly)
                    {
                        break;
                    }

                    UpdateStatViews();

                    switch (slot.ItemSubType)
                    {
                        case ItemSubType.Armor:
                        {
                            var armor = (Armor) _armorSlot.Item;
                            var weapon = (Weapon) _weaponSlot.Item;
                            player.EquipEquipmentsAndUpdateCustomize(armor, weapon);
                            break;
                        }
                        case ItemSubType.Weapon:
                            player.EquipWeapon((Weapon) _weaponSlot.Item);
                            break;
                    }

                    Game.Event.OnUpdatePlayerEquip.OnNext(player);
                    break;
                }
            }

            PostEquipOrUnequip(slot);
        }

        private void PostEquipOrUnequip(EquipmentSlot slot)
        {
            AudioController.instance.PlaySfx(slot.ItemSubType == ItemSubType.Food
                ? AudioController.SfxCode.ChainMail2
                : AudioController.SfxCode.Equipment);
            inventory.SharedModel.UpdateEquipmentNotification();
            Find<BottomMenu>().UpdateInventoryNotification();
        }

        private void LocalStateItemEquipModify(ItemBase itemBase, bool equip)
        {
            if (!(itemBase is INonFungibleItem nonFungibleItem))
            {
                return;
            }

            LocalLayerModifier.SetItemEquip(
                States.Instance.CurrentAvatarState.address,
                nonFungibleItem.NonFungibleId,
                equip);
        }

        private bool TryToFindSlotAlreadyEquip(ItemBase item, out EquipmentSlot slot)
        {
            switch (item.ItemType)
            {
                case ItemType.Costume:
                    return costumeSlots.TryGetAlreadyEquip(item, out slot);
                case ItemType.Equipment:
                    return equipmentSlots.TryGetAlreadyEquip(item, out slot);
                default:
                    slot = null;
                    return false;
            }
        }

        private bool TryToFindSlotToEquip(ItemBase item, out EquipmentSlot slot)
        {
            switch (item.ItemType)
            {
                case ItemType.Costume:
                    return costumeSlots.TryGetToEquip((Costume) item, out slot);
                case ItemType.Equipment:
                    return equipmentSlots.TryGetToEquip((Equipment) item, out slot);
                default:
                    slot = null;
                    return false;
            }
        }

        private void ShowTooltip(InventoryItemView view)
        {
            var tooltip = Find<ItemInformationTooltip>();
            if (view is null ||
                view.Model is null ||
                view.RectTransform == tooltip.Target)
            {
                tooltip.Close();

                return;
            }

            var (submitEnabledFunc, submitText, onSubmit) = GetToolTipParams(view.Model);
            tooltip.Show(
                view.RectTransform,
                view.Model,
                submitEnabledFunc,
                submitText,
                _ => onSubmit(view.Model),
                _ => inventory.SharedModel.DeselectItemView());
        }

        private void ShowTooltip(EquipmentSlot slot)
        {
            var tooltip = Find<ItemInformationTooltip>();
            if (slot is null ||
                slot.RectTransform == tooltip.Target)
            {
                tooltip.Close();

                return;
            }

            if (inventory.SharedModel.TryGetConsumable(slot.Item as Consumable, out var item) ||
                inventory.SharedModel.TryGetCostume(slot.Item as Costume, out item) ||
                inventory.SharedModel.TryGetEquipment(slot.Item as Equipment, out item))
            {
                var (submitEnabledFunc, submitText, onSubmit) = GetToolTipParams(item);
                tooltip.Show(
                    slot.RectTransform,
                    item,
                    submitEnabledFunc,
                    submitText,
                    _ => onSubmit(item),
                    _ => inventory.SharedModel.DeselectItemView());
            }
        }

        private (Func<CountableItem, bool>, string, Action<CountableItem>) GetToolTipParams(
            InventoryItem inventoryItem)
        {
            var item = inventoryItem.ItemBase.Value;
            Func<CountableItem, bool> submitEnabledFunc = null;
            string submitText = null;
            Action<CountableItem> onSubmit = null;
            switch (item.ItemType)
            {
                case ItemType.Consumable:
                    break;
                case ItemType.Costume:
                case ItemType.Equipment:
                    submitEnabledFunc = DimmedFuncForEquipments;
                    submitText = inventoryItem.EquippedEnabled.Value
                        ? L10nManager.Localize("UI_UNEQUIP")
                        : L10nManager.Localize("UI_EQUIP");
                    onSubmit = Equip;
                    break;
                case ItemType.Material:
                    switch (item.ItemSubType)
                    {
                        case ItemSubType.ApStone:
                            submitEnabledFunc = DimmedFuncForChargeActionPoint;
                            submitText = L10nManager.Localize("UI_CHARGE_AP");
                            onSubmit = ChargeActionPoint;
                            break;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return (submitEnabledFunc, submitText, onSubmit);
        }

        private bool DimmedFuncForChargeActionPoint(CountableItem item)
        {
            if (item is null || item.Count.Value < 1)
            {
                return false;
            }

            return States.Instance.CurrentAvatarState.actionPoint !=
                   States.Instance.GameConfigState.ActionPointMax
                   && !Game.Game.instance.Stage.IsInStage;
        }

        private bool DimmedFuncForChest(CountableItem item)
        {
            return !(item is null) && item.Count.Value >= 1 && !Game.Game.instance.Stage.IsInStage;
        }

        private bool DimmedFuncForEquipments(CountableItem item)
        {
            return !item.Dimmed.Value && !Game.Game.instance.Stage.IsInStage;
        }

        private static void ChargeActionPoint(CountableItem item)
        {
            if (item.ItemBase.Value is Nekoyume.Model.Item.Material material)
            {
                Notification.Push(Nekoyume.Model.Mail.MailType.System,
                    L10nManager.Localize("UI_CHARGE_AP"));
                Game.Game.instance.ActionManager.ChargeActionPoint();
                LocalLayerModifier.RemoveItem(States.Instance.CurrentAvatarState.address,
                    material.ItemId, 1);
                LocalLayerModifier.ModifyAvatarActionPoint(
                    States.Instance.CurrentAvatarState.address,
                    States.Instance.GameConfigState.ActionPointMax);
            }
        }

        public void TutorialActionClickAvatarInfoFirstInventoryCellView()
        {
            if (inventory.Scroll.TryGetFirstCell(out var cell))
            {
                inventory.SharedModel.SelectItemView(cell.View);
            }
            else
            {
                Debug.LogError(
                    $"TutorialActionClickAvatarInfoFirstInventoryCellView() throw error.");
            }
        }

        public void TutorialActionCloseAvatarInfoWidget() => Close();
    }
}
