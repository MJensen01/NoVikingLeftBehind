# "Moving items in the extra slots re-equips my Wisplight"

Reported by Matt on 0.10.2 (Valheim 1.0.12), client-side. Research done against the 1.0.12
decompile in `/opt/modlab/game/_dump/v1_0_12/assembly_valheim/`.

## 1. How an item in an NVLB equipment slot becomes "equipped"

The extra slots are ordinary cells of the player's own `Inventory` (rows 4+), so nothing about
sitting in a cell makes an item equipped. NVLB does that itself, in one place:

* `ExtraSlotsModule.cs:879` `InventoryChangedPostfix` - Harmony postfix on
  `Player.OnInventoryChanged` (patched at `ExtraSlotsModule.cs:364`) - calls `SyncEquipment`.
* `ExtraSlotsModule.cs:893` `SyncEquipment(Player)` walks **every** equipment-kind slot
  (`SlotLayout.IsEquipmentKind` = Helmet/Chest/Legs/Cape/Utility, `SlotLayout.cs:279`) and:
  * utility slots 2+ have no vanilla field (`Humanoid.m_utilityItem` is a single reference), so
    they are held in NVLB's own `ExtraUtility[3]` array (`:51`) and faked equipped:
    `ExtraUtility[idx] = item; item.m_equipped = true;` (`:930-931`), then `SetupEquipment()`
    (`:941`) and `UpdateEquipSePostfix` (`:948`) re-adds their `m_equipStatusEffect`.
  * every other equipment slot, including utility slot 1, goes through vanilla:
    **`if (!p.IsItemEquiped(item)) p.EquipItem(item, false);`** (`:937`).

There is no separate login-restore equip pass: login works because `Player.Load` ->
`SlotStore.Inject` fires `Inventory.Changed` and the same `SyncEquipment` runs.

`ExtraUtility` is released in `UnequipItemPrefix` (`:976`, prefix on `Humanoid.UnequipItem`),
`UnequipAllPostfix` (`:989`) and at the top of `SyncEquipment` when the item is no longer in the
slot (`:903-914`). `IsItemEquipedPostfix` (`:968`) makes vanilla agree that an `ExtraUtility`
item is equipped.

## 2. Every client path that equips/uses an item

| Path | File:line | Trigger |
|---|---|---|
| `p.EquipItem(item, false)` | `ExtraSlotsModule.cs:937` | **any inventory change** |
| `ExtraUtility[idx]=item; m_equipped=true` | `ExtraSlotsModule.cs:930` | **any inventory change** |
| `UseItem` quick-slot hotkey | `ExtraSlotsModule.cs:1044` | player presses a quick key |
| `UseItem` auto-eat | `ExtraSlotsModule.cs:1074` | food buff expired, once/sec |
| `EquipItem/UnequipItem` loadouts | `LoadoutsModule.cs:472-526` | player runs a loadout |

Only the first two are automatic. Both hang off `Player.OnInventoryChanged`.

## 3. What a drag inside the extra-slot panel actually does

Vanilla `InventoryGui.OnSelectedItem` (`InventoryGui.cs:882`) on a drop:

```
RemoveEquipAction(item); RemoveEquipAction(m_dragItem);
UnequipItem(m_dragItem, false);            // :907
UnequipItem(item, false);                  // :908  (item = whatever was in the target cell)
grid.DropItem(...)                         // :909  -> Inventory.RemoveItem/AddItem -> Changed()
   -> Player.OnInventoryChanged -> NVLB InventoryChangedPostfix -> SyncEquipment   <-- HERE
if (flag)  ... EquipItem(...)              // :913-925 vanilla re-equips what was equipped
```

So one drag-and-drop inside the panel fires `SyncEquipment` at least once (usually twice -
`Inventory.RemoveItem` and `AddItem` both call `Changed()`, `Inventory.cs:1131/1137`), and
`SyncEquipment` has no idea which item the player touched: it equips **anything** found in an
equipment slot that is not currently equipped. NVLB's only drag patch is `GridDropPrefix`
(`:743`), which just refuses items the slot does not accept; it does not equip.

## 4. The vanilla utility rule

`Humanoid.EquipItem` for `ItemType.Utility` (`Humanoid.cs:1244-1252`) unequips the previous
`m_utilityItem` and takes the slot; `ToggleEquipped` (`Humanoid.cs:1014`, `Player.cs:7231`) -
what right-click in the inventory does, via `Humanoid.UseItem` (`:925`) - unequips an equipped
item and equips an unequipped one. Nothing in vanilla ever re-equips an item because the
inventory changed.

## 5. Is the Wisplight special?

Only in how visible it is. `Demister` is `ItemType.Utility` with an `m_equipStatusEffect`
(the wisp ball + Demister SE), so a re-equip is *seen and heard* instantly, where a re-equipped
belt or cape is silent. NVLB never re-equips by status effect - `UpdateEquipSePostfix` (`:948`)
only re-adds the SE of items already held in `ExtraUtility`. So the wisp is not a special case,
it is the loudest symptom of a general rule.

## 6. Mechanism, ranked

1. **(high confidence) `SyncEquipment` force-equips anything sitting in an equipment slot on
   every inventory change** - `ExtraSlotsModule.cs:937` (utility 1 / armour) and `:928-931`
   (utility 2+). The player right-clicks the Wisplight to turn the light off; the item stays in
   its utility cell; the very next inventory change - i.e. moving *any* item in the panel -
   re-equips it. That is exactly "every time I move items around in my extra item slots it
   automatically equips the Wisplight", and it makes an unequipped-but-parked utility item
   impossible to keep.
2. (medium) The same line fires mid-drag: vanilla unequips the drag item and the target item at
   `InventoryGui.cs:907-908` *before* `DropItem`, and `DropItem`'s `Changed()` re-enters
   `SyncEquipment` while both are unequipped, so NVLB can re-equip an item vanilla was about to
   move, then vanilla re-equips again at `:913-925`. Same root cause, same fix.
3. (low) `ExtraUtility` claim churn: an item dropped into utility 2+ is claimed by NVLB
   (`:930`) and released by `UnequipItemPrefix` (`:976`), and those two can fight over one drag,
   producing a `SetupEquipment()` + SE re-add (a visible wisp pop) with no vanilla equip at all.

## 7. Diagnostic plan

Local-only `[Slots] Diagnostics` (same shape as `[Chests] Diagnostics`,
`CraftFromChestsModule.cs:222/411`): one throttled line for every equip/release NVLB itself
performs, naming the item, the slot and the reason, plus one line per drop into the panel. A log
from Matt then shows either `equip Demister because ... utility1` lines following each drop
(mechanism 1) or nothing, which would move the search to vanilla's own `flag`/`flag2` re-equip.

## 8. Fix

`SyncEquipment` remembers which item object it last saw in each equipment slot
(`_lastInSlot`, keyed by slot key) and equips only an item that is **new to that slot** - i.e.
one that just arrived there. An item that has been sitting in the slot since the last sync is
left exactly as the player left it, so a deliberate unequip sticks and no inventory move can
re-equip anything. Login/respawn still equips everything because the map starts empty (it is
cleared in `PlayerLoadPrefix`/`PlayerSpawnedPostfix`), and dropping a helmet into the head slot
still equips it, because that item *is* new to the slot.
