import {
  el,
  frag,
  clear,
  button,
  input,
  field,
  toast,
  modal,
  confirmDialog,
  notifyRestart,
  openPicker,
  promptAmount,
  num,
  stars,
  escapeHtml,
  emptyState,
  select,
} from '../ui.js';
import { icon } from '../icons.js';
import { api, targetAccount } from '../api.js';
import { gate, loadInto } from './_util.js';
import { openTuneDialog } from '../tune.js';

export default {
  id: 'inventory',
  title: 'Inventory',
  icon: 'inventory',
  needsTarget: true,

  mount(root) {
    return gate(root, { needServer: true, needTarget: true }, (root) => {
      const acc = targetAccount();
      const uid = acc.serverId;

      // Student level ceiling is the account level: an account at 70 cannot hold level 90 students.
      // Declared before any paint so the row handlers never read it out of order.
      const levelCap = Math.min(90, Number(acc.level) > 0 ? Number(acc.level) : 90);

      // Default state for bulk: level applied, every other field left alone. Presets fill in the rest, and
      // the star field starts unticked so a bulk run never flattens everyone to one rarity.
      const maxedInitial = () => ({
        level: levelCap,
        levelEnabled: true,
        star: 5,
        starEnabled: false,
        skillEnabled: false,
        exEnabled: false,
        potentialEnabled: false,
        ueEnabled: false,
        gearEnabled: false,
        gearLevel: 1,
        gearIncludeExisting: false,
      });

      // Highest equipment level in the server's tables; fetched once and used to bound the slider.
      let gearMaxLevel = 70;
      api
        .gearMeta()
        .then((r) => {
          if (r && r.maxLevel > 0) gearMaxLevel = r.maxLevel;
        })
        .catch(() => {});

      const bulk = el(
        'div.card',
        { style: { marginBottom: '18px' } },
        el(
          'div.card-head',
          { style: { flexWrap: 'wrap', rowGap: '4px' } },
          el('span.tab-mark', {}),
          el('h3', { text: 'Bulk grants' }),
          el(
            'span.sub',
            { style: { minWidth: '0', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } },
            `for ${acc.nickname} - `,
            el('span.mono', { text: `#${uid}`, 'data-selectable': '' }),
          ),
        ),
        el(
          'div.card-body',
          { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', minWidth: '0' } },
          cmdBtn(uid, 'All items', 'box', 'inventory add items', 'ghost', () => reloadItems(), 'Grants every item at max stack size.'),
          cmdBtn(
            uid,
            'All equipment items',
            'shield',
            'giveallequip',
            'ghost',
            null,
            'Grants every equipment piece as an inventory item, at max level. Nothing is equipped.',
          ),
          cmdBtn(
            uid,
            'All characters',
            'users',
            'giveall',
            'ghost',
            () => reloadChars(),
            'Unlocks every student so it shows up in the roster.',
          ),
          button('Bulk tune...', { variant: 'primary', sm: true, iconName: 'star', onClick: () => bulkTune(charRows) }),
        ),
      );
      root.appendChild(bulk);

      const dangerCard = el(
        'div.card',
        { style: { marginTop: '18px', border: '1px solid rgba(255,96,96,.35)' } },
        el(
          'div.card-head',
          {},
          el('span.tab-mark', {}),
          el('h3', { text: 'Danger' }),
          el('span.sub', { text: 'A database snapshot is taken before this runs' }),
          el('div.spacer', {}),
        ),
        el(
          'div.card-body',
          { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', minWidth: '0', padding: '18px 20px 16px' } },
          dangerCmd(uid, 'Clear inventory', 'trash', 'clearinventory', () => reloadItems()),
          button('Clear characters', { variant: 'danger', sm: true, iconName: 'users', onClick: clearCharacters }),
        ),
      );
      root.appendChild(dangerCard);

      const grid = el('div.grid-2', { style: { alignItems: 'start', marginTop: '18px' } });
      root.appendChild(grid);

      const itemSearch = input({
        placeholder: 'Filter...',
        className: 'input btn-sm',
        style: { height: '32px', width: '120px', minWidth: '0', flex: '0 1 120px' },
      });
      const giveItemBtn = button('Give item', { variant: 'primary', sm: true, iconName: 'plus', onClick: giveItem });
      const itemsCard = el(
        'div.card',
        {},
        el(
          'div.card-head',
          { style: { flexWrap: 'wrap', rowGap: '8px' } },
          el('span.tab-mark', {}),
          el('h3', { text: 'Items' }),
          el('div.spacer', {}),
          itemSearch,
          giveItemBtn,
        ),
      );
      const itemsBody = el('div.list-scroll', { style: { maxHeight: '58vh' } });
      itemsCard.appendChild(itemsBody);
      grid.appendChild(itemsCard);
      let itemRows = [];
      itemSearch.addEventListener('input', paintItems);

      function paintItems() {
        const q = itemSearch.value.trim().toLowerCase();
        const rows = itemRows.filter((r) => !q || r.name.toLowerCase().includes(q) || String(r.uniqueId).includes(q));
        clear(itemsBody);
        if (!rows.length) {
          itemsBody.appendChild(emptyState('No items'));
          return;
        }
        // 64px = 36px trash button + the 28px of cell padding (border-box) - narrower and the button overflows the fixed column, dragging a horizontal scrollbar into the list
        const tbl = frag(
          '<table class="tbl" style="table-layout:fixed"><thead><tr><th style="width:74px">ID</th><th>Name</th><th style="width:72px">Qty</th><th style="width:64px"></th></tr></thead><tbody></tbody></table>',
        );
        const tb = tbl.querySelector('tbody');
        for (const r of rows) {
          const tr = frag(
            `<tr><td class="num mono" data-selectable>${r.uniqueId}</td><td style="min-width:0;max-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${escapeHtml(r.name)}">${escapeHtml(r.name)}</td><td class="num">${num(r.stackCount)}</td><td></td></tr>`,
          );
          const x = button('', { variant: 'ghost', sm: true, iconName: 'trash' });
          x.style.height = '26px';
          x.style.padding = '0 8px';
          x.addEventListener('click', async (e) => {
            e.stopPropagation();
            await api.removeItem({ accountServerId: uid, uniqueId: r.uniqueId });
            toast('Item removed', 'warn');
            notifyRestart();
            reloadItems();
          });
          tr.lastElementChild.appendChild(x);
          tb.appendChild(tr);
        }
        itemsBody.appendChild(tbl);
      }
      async function reloadItems() {
        await loadInto(
          itemsBody,
          () => api.items(uid),
          (_b, rows) => {
            itemRows = rows;
            paintItems();
          },
        );
      }

      function giveItem() {
        openPicker({
          title: 'Pick an item',
          loader: (q) => api.staticItems(q).then((r) => r.map((x) => ({ id: x.id, name: x.name, sub: x.icon }))),
          onPick: (it) =>
            promptAmount({
              title: `Give ${it.name}`,
              confirmLabel: 'Grant',
              onConfirm: async (amount) => {
                await api.giveItem({ accountServerId: uid, uniqueId: it.id, amount });
                toast(`Gave ${num(amount)}× ${it.name}`, 'good');
                notifyRestart();
                reloadItems();
              },
            }),
        });
      }

      const charSearch = input({
        placeholder: 'Filter...',
        className: 'input btn-sm',
        style: { height: '32px', width: '120px', minWidth: '0', flex: '0 1 120px' },
      });
      const addCharBtn = button('Add character', { variant: 'primary', sm: true, iconName: 'plus', onClick: addChar });
      const maxFilteredWrap = el('div', { style: { display: 'contents' } });
      const charsCard = el(
        'div.card',
        {},
        el(
          'div.card-head',
          { style: { flexWrap: 'wrap', rowGap: '8px' } },
          el('span.tab-mark', {}),
          el('h3', { text: 'Characters' }),
          addCharBtn,
          el('div.spacer', {}),
          maxFilteredWrap,
          charSearch,
        ),
      );
      const charsBody = el('div.list-scroll', { style: { maxHeight: '58vh' } });
      charsCard.appendChild(charsBody);
      grid.appendChild(charsCard);
      let charRows = [];
      charSearch.addEventListener('input', paintChars);

      function paintChars() {
        const q = charSearch.value.trim().toLowerCase();
        const rows = charRows.filter((r) => !q || r.name.toLowerCase().includes(q) || String(r.uniqueId).includes(q));
        clear(charsBody);
        clear(maxFilteredWrap);
        if (rows.length && rows.length < charRows.length) {
          maxFilteredWrap.appendChild(
            button(`Max filtered (${rows.length})`, {
              variant: 'ghost',
              sm: true,
              iconName: 'star',
              onClick: () => bulkTune(rows),
            }),
          );
        }
        if (!rows.length) {
          charsBody.appendChild(emptyState('No characters'));
          return;
        }
        const tbl = frag(
          '<table class="tbl" style="table-layout:fixed"><thead><tr><th style="width:74px">ID</th><th>Name</th><th style="width:84px">Grade</th><th style="width:46px">Lvl</th><th style="width:44px"></th></tr></thead><tbody></tbody></table>',
        );
        const tb = tbl.querySelector('tbody');
        for (const r of rows) {
          const tr = frag(
            `<tr><td class="num mono" data-selectable>${r.uniqueId}</td><td style="min-width:0;max-width:0"><b style="font-family:var(--font-round);display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${escapeHtml(r.name)}">${escapeHtml(r.name)}</b></td><td class="stars"></td><td class="num">${r.level}</td><td></td></tr>`,
          );
          tr.title = 'Click to max this student';
          const starCell = tr.querySelector('.stars');
          for (let i = 1; i <= 5; i++) {
            const s = el('span', { text: i <= r.starGrade ? '★' : '☆', title: `Set to ${i}★`, style: { cursor: 'pointer' } });
            s.addEventListener('click', async (e) => {
              e.stopPropagation();
              try {
                await api.command(uid, `character modify ${r.uniqueId} star ${i}`);
                toast(`${r.name} set to ${i}★`, 'good');
                notifyRestart();
                reloadChars();
              } catch (err) {
                toast(err.message, 'bad');
              }
            });
            starCell.appendChild(s);
          }

          const rm = button('', { variant: 'ghost', sm: true, iconName: 'trash' });
          rm.style.cssText = 'height:26px;padding:0 8px';
          rm.title = `Remove ${r.name}`;
          rm.addEventListener('click', async (e) => {
            e.stopPropagation();
            const ok = await confirmDialog({
              title: 'Remove student',
              danger: true,
              confirmLabel: 'Remove',
              message: `Remove ${r.name} (#${r.uniqueId}) from ${acc.nickname}. Gear and the unique weapon stay in the database as unbound rows.`,
              detail: `character remove ${r.uniqueId}`,
            });
            if (!ok) return;
            const snap = await window.host
              .backupCreate('pre-remove-student')
              .catch((err) => ({ ok: false, error: String(err.message || err) }));
            if (!snap || !snap.ok) toast(`Snapshot skipped: ${snap ? snap.error : 'unknown'}`, 'warn');
            try {
              const res = await api.command(uid, `character remove ${r.uniqueId}`);
              toast((res && res.output) || 'Student removed', 'warn');
              notifyRestart();
              reloadChars();
            } catch (err) {
              toast(err.message, 'bad');
            }
          });
          tr.lastElementChild.appendChild(rm);

          tr.addEventListener('click', () => {
            openTune(`id:${r.uniqueId}`, {
              title: r.name,
              subtitle: `Currently Lv ${r.level}, ${r.starGrade}★. Only ticked fields are written, so it can be one attribute or all of them.`,
              starMin: Math.max(1, r.defaultStar || 1),
              starMax: Math.max(5, r.maxStar || 5),
              initial: {
                level: r.level,
                star: Math.max(r.starGrade, r.defaultStar || 1),
                levelEnabled: true,
                starEnabled: true,
              },
            });
          });
          tb.appendChild(tr);
        }
        charsBody.appendChild(tbl);
      }
      async function reloadChars() {
        await loadInto(
          charsBody,
          () => api.characters(uid),
          (_b, rows) => {
            charRows = rows;
            paintChars();
          },
        );
      }

      function addChar() {
        openPicker({
          title: 'Pick a student',
          loader: (q) => api.staticCharacters(q).then((r) => r.map((x) => ({ id: x.id, name: x.name, sub: `★${x.maxStar}` }))),
          onPick: (it) => {
            const preset = select(
              [
                { value: 'barebone', label: 'barebone — fresh unlock: default stars, Lv1, skills 1, no gear' },
                { value: 'basic', label: 'basic — default stars, max level and skills, favor 20, equipment + gear' },
                { value: 'ue30', label: 'ue30 — 5★, unique weapon Lv30, favor 20, equipment + gear' },
                { value: 'ue50', label: 'ue50 — 5★, unique weapon Lv50, favor 50, equipment + gear' },
                { value: 'max', label: 'max — 5★, unique weapon Lv60, favor 100, potentials, equipment + gear' },
              ],
              { value: 'max' },
            );
            const add = button('Add', { variant: 'primary', iconName: 'plus' });
            const cancel = button('Cancel', { variant: 'ghost' });
            const ref = modal({
              title: `Add ${it.name}`,
              body: el('div', {}, field('Preset', preset, 'Equipment and gear only fill empty slots.')),
              footer: [cancel, add],
            });
            cancel.addEventListener('click', ref.close);
            add.addEventListener('click', async () => {
              const opt = preset.value;
              try {
                await api.command(uid, `character add ${it.id} ${opt}`);
                ref.close();
                toast(`Added ${it.name} (${opt})`, 'good');
                notifyRestart();
                reloadChars();
              } catch (e) {
                toast(e.message, 'bad');
              }
            });
          },
        });
      }

      reloadItems();
      reloadChars();

      // Only ticked fields become tokens, so the dialog can change one attribute without touching the rest.
      function buildApplyCommand(target, v) {
        const parts = [`apply ${target}`];
        if (v.level.enabled) parts.push(`level:${v.level.value}`);
        if (v.star.enabled) parts.push(v.star.base ? 'star:base' : `star:${v.star.value}`);
        if (v.skill.enabled) parts.push(`skill:${v.skill.value}`);
        if (v.ex.enabled) parts.push(`ex:${v.ex.value}`);
        if (v.potential.enabled) parts.push(`ps:${v.potential.value}`);
        if (v.ue.enabled) parts.push(`ue:${v.ue.value}`);
        if (v.gear.enabled) {
          parts.push('gear:1');
          parts.push(`gearlvl:${v.gear.level}`);
          parts.push(`geartier:${v.gear.tier}`);
          if (v.gear.includeExisting) parts.push('gearinclude:1');
        }
        return parts.length > 1 ? parts.join(' ') : '';
      }

      function openTune(target, { title, subtitle, initial = {}, starMin = 1, starMax = 5, presets = [], hints }) {
        openTuneDialog({
          title,
          subtitle,
          target,
          initial,
          starMin,
          starMax,
          levelMax: levelCap,
          gearMax: gearMaxLevel,
          presets,
          hints,
          buildCommand: (v) => buildApplyCommand(target, v),
          onApply: (command) => runCommand(command, title, null, () => reloadChars(), { skipConfirm: true }),
        });
      }

      // One dialog for the whole bulk path: presets cover the old one-click actions, and the sliders
      // keep everything in between reachable. The star field starts unticked so a bulk run never
      // flattens every student to the same rarity.
      function bulkTune(rows) {
        const fiveStars = rows.filter((r) => (r.starGrade || 0) >= 5).length;
        const target = rows.length === charRows.length ? 'all' : `id:${rows.map((r) => r.uniqueId).join(',')}`;

        openTune(target, {
          title: rows.length === charRows.length ? 'Bulk tune - all students' : `Bulk tune - ${rows.length} filtered`,
          subtitle: `${rows.length} student(s) on ${acc.nickname}. Unticked fields are not sent at all.`,
          initial: maxedInitial(),
          presets: [
            {
              label: 'Max everything',
              values: {
                level: { value: levelCap },
                star: { value: 5 },
                skill: { value: 10 },
                ex: { value: 5 },
                potential: { value: 25 },
                ue: { value: 60 },
                gear: { value: gearMaxLevel },
                gearTier: 10,
                gearIncludeExisting: true,
              },
            },
            { label: 'Level only', values: { level: { value: levelCap } } },
            { label: 'UE only', values: { ue: { value: 60 } } },
            { label: 'Stars to base', values: { star: { base: true } } },
            { label: 'Gear at Lv1', values: { gear: { value: 1 }, gearTier: 1 } },
            { label: 'Gear at max', values: { gear: { value: gearMaxLevel }, gearTier: 10 } },
          ],
          hints: (v) => ({
            star: v.star.enabled
              ? `Every targeted student becomes ${v.star.value}★ (never below its own base rarity)`
              : `${fiveStars} of ${rows.length} are already 5★; the rest keep their rarity`,
            ue:
              v.star.enabled && v.star.value >= 5
                ? `Applies to all ${rows.length} students - they all become 5★ with this apply`
                : `Applies to ${fiveStars} of ${rows.length} students, the ones already at 5★`,
          }),
        });
      }

      async function runCommand(command, label, desc, after, opts = {}) {
        if (!opts.skipConfirm) {
          const ok = await confirmDialog({
            title: label,
            message: desc ? `${desc} Account: ${acc.nickname} (#${uid}).` : `Run "${label}" for ${acc.nickname} (#${uid})?`,
            detail: command,
            confirmLabel: label,
            danger: !!opts.danger,
          });
          if (!ok) return;
        }

        // Snapshot first: a console command cannot be undone from the UI.
        const snap = await window.host
          .backupCreate(
            label
              .toLowerCase()
              .replace(/[^a-z0-9]+/g, '-')
              .slice(0, 32),
          )
          .catch((e) => ({ ok: false, error: String(e.message || e) }));
        if (!snap || !snap.ok) toast(`Snapshot skipped: ${snap ? snap.error : 'unknown error'}`, 'warn');

        try {
          const res = await api.command(uid, command);
          const out = (res && res.output ? String(res.output).trim() : '') || `${label} done`;
          toast(out.split('\n').filter(Boolean).slice(0, 2).join(' '), opts.danger ? 'warn' : 'good');
          notifyRestart();
          after && after();
        } catch (e) {
          toast(e.message, 'bad');
        }
      }

      // Clearing the roster keeps the four starters by default, because the account always needs at
      // least the lobby student; the checkbox narrows it down to that student only.
      async function clearCharacters() {
        // The kept student is the account's Student Rep. Resolve its name first so the dialog states
        // who survives instead of leaving the operator to guess between Hasumi and Yuuka.
        let repName = null;
        try {
          const [detail, chars] = await Promise.all([api.accountDetail(uid), api.characters(uid)]);
          const repServerId = detail && (detail.representCharacterServerId ?? detail.RepresentCharacterServerId);
          const row = (chars || []).find((c) => (c.serverId ?? c.ServerId) === repServerId);
          repName = row ? row.name || row.devName : null;
        } catch {
          /* the name is a nicety; the command itself does not need it */
        }

        const repLabel = repName ? `the Student Rep of this account (${repName})` : 'the Student Rep of this account';

        const hard = el('input', {
          type: 'checkbox',
          style: { accentColor: 'var(--accent)', width: '15px', height: '15px', cursor: 'pointer' },
        });

        const body = el(
          'div',
          { style: { fontSize: '13.5px', color: 'var(--ink-2)', lineHeight: '1.6', minWidth: '0' } },
          el('p', {
            text: `Removes every student on ${acc.nickname} except the starting students. Those are reset to level 1, base rarity, skill 1, no gear.`,
          }),
          el(
            'label',
            { style: { display: 'grid', gridTemplateColumns: '15px 1fr', columnGap: '10px', alignItems: 'start', marginTop: '10px' } },
            hard,
            el(
              'div',
              { style: { minWidth: '0' } },
              el('div', {
                text: 'Also remove the starting students',
                style: { fontSize: '13px', color: 'var(--ink-1)', fontWeight: '600' },
              }),
              el('div', {
                text: `Only ${repLabel} is kept. The lobby always shows one student, so an empty roster is not a state the client handles.`,
                style: { fontSize: '11.5px', color: 'var(--ink-3)', marginTop: '2px' },
              }),
            ),
          ),
          el('div', {
            text: 'A database snapshot is taken first, so this can be restored from Updates.',
            style: { fontSize: '12px', color: 'var(--ink-3)', marginTop: '12px', opacity: '.85' },
          }),
        );

        const cancel = button('Cancel', { variant: 'ghost' });
        const go = button('Clear characters', { variant: 'danger', iconName: 'trash' });
        const ref = modal({ title: 'Clear characters', body, footer: [cancel, go] });

        cancel.addEventListener('click', () => ref.close());
        go.addEventListener('click', async () => {
          const command = hard.checked ? 'character clear all hard' : 'character clear all';
          ref.close();
          await runCommand(
            command,
            'Clear characters',
            null,
            () => {
              reloadChars();
              reloadItems();
            },
            { skipConfirm: true, danger: true },
          );
        });
      }

      function cmdBtn(uid, label, ic, command, variant, after, desc) {
        return button(label, { variant, sm: true, iconName: ic, onClick: () => runCommand(command, label, desc, after) });
      }
      function dangerCmd(uid, label, ic, command, after) {
        // The message is built inside the handler: itemRows is declared further down this scope, so
        // reading it while the button is constructed hits the temporal dead zone and aborts the render.
        return button(label, {
          variant: 'danger',
          sm: true,
          iconName: ic,
          onClick: () => {
            const msg = `Deletes ${itemRows.length} item rows and every unbound equipment piece for ${acc.nickname}. Students, currencies and mail are not touched.`;
            return runCommand(command, label, msg, after, { danger: true });
          },
        });
      }
    });
  },
};
