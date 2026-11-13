import { el, button, modal } from './ui.js';

// A tuning dialog built from independent controls. Each attribute has its own checkbox, because the
// checkbox carries intent ("touch this field") while the slider only carries a value - that is what
// makes "set level 40 but leave skills alone" expressible instead of all-or-nothing.

function controlRow({ label, hint, min, max, value, enabled, format, step = 1 }) {
  const state = { enabled: !!enabled, value: Math.min(max, Math.max(min, value)) };

  const check = el('input', {
    type: 'checkbox',
    checked: state.enabled,
    style: { accentColor: 'var(--accent)', width: '15px', height: '15px', flex: '0 0 auto', cursor: 'pointer' },
  });

  const readout = el('span.mono', {
    text: format(state.value),
    style: { minWidth: '74px', textAlign: 'right', fontSize: '13px', color: 'var(--ink-2)' },
  });

  const range = el('input', {
    type: 'range',
    min: String(min),
    max: String(max),
    step: String(step),
    value: String(state.value),
    style: { flex: '1 1 0', minWidth: '0', accentColor: 'var(--accent)', cursor: 'pointer' },
  });

  const hintEl = el('div', {
    text: hint || '',
    style: { fontSize: '11.5px', color: 'var(--ink-3)', marginTop: '1px', lineHeight: '1.35' },
  });

  const paint = () => {
    readout.textContent = format(state.value);
    range.disabled = !state.enabled;
    range.style.opacity = state.enabled ? '1' : '.35';
    readout.style.opacity = state.enabled ? '1' : '.45';
  };

  range.addEventListener('input', () => {
    state.value = Number(range.value);
    // Dragging a slider is an unambiguous statement of intent, so it switches the field on.
    if (!state.enabled) {
      state.enabled = true;
      check.checked = true;
    }
    paint();
  });

  check.addEventListener('change', () => {
    state.enabled = check.checked;
    paint();
  });

  paint();

  const row = el(
    'div',
    {
      style: {
        display: 'grid',
        gridTemplateColumns: '15px minmax(96px, 132px) 1fr 74px',
        alignItems: 'center',
        columnGap: '10px',
        padding: '9px 0',
        borderTop: '1px solid rgba(255,255,255,.06)',
        minWidth: '0',
      },
    },
    check,
    el(
      'div',
      { style: { minWidth: '0' } },
      el('div', { text: label, style: { fontSize: '13px', color: 'var(--ink-1)', fontWeight: '600' } }),
      hintEl,
    ),
    range,
    readout,
  );

  return {
    row,
    get() {
      return { enabled: state.enabled, value: state.value };
    },
    set(enabled, nextValue) {
      state.enabled = !!enabled;
      if (nextValue != null) state.value = Math.min(max, Math.max(min, nextValue));
      check.checked = state.enabled;
      range.value = String(state.value);
      paint();
    },
    setHint(text) {
      hintEl.textContent = text || '';
    },
  };
}

// Equipment tiers are bands of level and each band starts at the previous tier's maximum, because
// evolution keeps the level (a T2 item sits at Lv10, never below). Placement uses the LOWEST tier that
// reaches the requested level, so no impossible state is ever written.
const GEAR_REQUIRED_TIER = [
  [10, 1],
  [20, 2],
  [30, 3],
  [40, 4],
  [45, 5],
  [50, 6],
  [55, 7],
  [60, 8],
  [65, 9],
  [70, 10],
];

const GEAR_TIER_MIN_LEVEL = { 1: 1, 2: 10, 3: 20, 4: 30, 5: 40, 6: 45, 7: 50, 8: 55, 9: 60, 10: 65 };
const GEAR_TIER_MAX_LEVEL = { 1: 10, 2: 20, 3: 30, 4: 40, 5: 45, 6: 50, 7: 55, 8: 60, 9: 65, 10: 70 };

function gearTierForLevel(level) {
  const hit = GEAR_REQUIRED_TIER.find(([max]) => level <= max);
  return hit ? hit[1] : 10;
}

// Highest tier the level has already unlocked: level 10 may be evolved to T2, level 29 still cannot.
function gearMaxTierForLevel(level) {
  let best = 1;
  for (const [tier, minLevel] of Object.entries(GEAR_TIER_MIN_LEVEL)) {
    if (level >= minLevel) best = Math.max(best, Number(tier));
  }
  return best;
}

// Slider without its own checkbox: used where the field only means something together with the field
// above it (an equipment tier is meaningless unless gear is being placed), so it follows that switch.
function plainSlider({ label, hint, min, max, value, format }) {
  const state = { value: Math.min(max, Math.max(min, value)) };
  const readout = el('span.mono', {
    text: format(state.value),
    style: { minWidth: '74px', textAlign: 'right', fontSize: '13px', color: 'var(--ink-2)' },
  });
  const range = el('input', {
    type: 'range',
    min: String(min),
    max: String(max),
    step: '1',
    value: String(state.value),
    style: { flex: '1 1 0', minWidth: '0', accentColor: 'var(--accent)', cursor: 'pointer' },
  });
  const hintEl = el('div', {
    text: hint || '',
    style: { fontSize: '11.5px', color: 'var(--ink-3)', marginTop: '1px', lineHeight: '1.35' },
  });

  range.addEventListener('input', () => {
    state.value = Number(range.value);
    readout.textContent = format(state.value);
  });

  const row = el(
    'div',
    {
      style: {
        display: 'grid',
        gridTemplateColumns: '15px minmax(96px, 132px) 1fr 74px',
        alignItems: 'center',
        columnGap: '10px',
        padding: '9px 0',
        borderTop: '1px solid rgba(255,255,255,.06)',
        minWidth: '0',
      },
    },
    el('div'),
    el(
      'div',
      { style: { minWidth: '0' } },
      el('div', { text: label, style: { fontSize: '13px', color: 'var(--ink-1)', fontWeight: '600' } }),
      hintEl,
    ),
    range,
    readout,
  );

  return {
    row,
    get() {
      return state.value;
    },
    setValue(nextValue) {
      state.value = Math.min(max, Math.max(min, nextValue));
      range.value = String(state.value);
      readout.textContent = format(state.value);
    },
    setBounds(nextMin, nextMax) {
      range.min = String(nextMin);
      range.max = String(nextMax);
      state.value = Math.min(nextMax, Math.max(nextMin, state.value));
      range.value = String(state.value);
      readout.textContent = format(state.value);
    },
    setHint(text) {
      hintEl.textContent = text || '';
    },
    setEnabled(on) {
      range.disabled = !on;
      range.style.opacity = on ? '1' : '.35';
      readout.style.opacity = on ? '1' : '.45';
      hintEl.style.opacity = on ? '1' : '.45';
    },
  };
}

export function openTuneDialog({
  title,
  subtitle,
  target,
  initial = {},
  starMin = 1,
  starMax = 5,
  levelMax = 90,
  gearMax = 70,
  presets = [],
  hints,
  buildCommand,
  onApply,
}) {
  const level = controlRow({
    label: 'Level',
    hint: `1 - ${levelMax}, capped by the account level`,
    min: 1,
    max: levelMax,
    value: initial.level ?? levelMax,
    enabled: initial.levelEnabled !== false,
    format: (v) => `Lv ${v}`,
  });

  const star = controlRow({
    label: 'Star',
    hint: `${starMin}★ - ${starMax}★. Unticked keeps every student's own rarity.`,
    min: starMin,
    max: starMax,
    value: initial.star ?? starMax,
    enabled: initial.starEnabled !== false,
    format: (v) => `${v}★`,
  });

  const starBaseCheck = el('input', {
    type: 'checkbox',
    checked: initial.starBase === true,
    style: { accentColor: 'var(--accent)', width: '15px', height: '15px', cursor: 'pointer' },
  });
  starBaseCheck.addEventListener('change', () => {
    // Ticking this is a statement of intent on its own, so the star field switches on with it.
    if (starBaseCheck.checked) star.set(true, null);
  });
  const starBaseRow = el(
    'div',
    {
      style: {
        display: 'grid',
        gridTemplateColumns: '15px 1fr',
        alignItems: 'center',
        columnGap: '10px',
        padding: '10px 0',
        borderTop: '1px solid rgba(255,255,255,.06)',
      },
    },
    starBaseCheck,
    el(
      'div',
      { style: { minWidth: '0' } },
      el('div', { text: 'Back to base rarity', style: { fontSize: '13px', color: 'var(--ink-1)', fontWeight: '600' } }),
      el('div', {
        text: 'Each student returns to its own base star (1★, 2★ or 3★); the slider above is ignored while this is on.',
        style: { fontSize: '11.5px', color: 'var(--ink-3)', marginTop: '1px' },
      }),
    ),
  );

  const skills = controlRow({
    label: 'Skills',
    hint: 'public / passive / extra',
    min: 1,
    max: 10,
    value: initial.skill ?? 10,
    enabled: initial.skillEnabled === true,
    format: (v) => `Lv ${v}`,
  });

  const ex = controlRow({
    label: 'EX skill',
    hint: '1 - 5',
    min: 1,
    max: 5,
    value: initial.ex ?? 5,
    enabled: initial.exEnabled === true,
    format: (v) => `Lv ${v}`,
  });

  const potential = controlRow({
    label: 'Potential',
    hint: '0 - 25, all three slots',
    min: 0,
    max: 25,
    value: initial.potential ?? 25,
    enabled: initial.potentialEnabled === true,
    format: (v) => `${v}`,
  });

  const weapon = controlRow({
    label: 'Unique equipment',
    hint: 'level 1 - 60, 5★ students only',
    min: 1,
    max: 60,
    value: initial.ue ?? 60,
    enabled: initial.ueEnabled === true,
    format: (v) => `Lv ${v}`,
  });

  const gear = controlRow({
    label: 'Gear',
    hint: `equip into empty slots at Lv 1 - ${gearMax}`,
    min: 1,
    max: gearMax,
    value: initial.gearLevel ?? 1,
    enabled: initial.gearEnabled === true,
    format: (v) => `Lv ${v}`,
  });

  const gearTier = plainSlider({
    label: 'Gear tier',
    hint: 'follows the level: it rises when the level enters the next band',
    min: 1,
    max: 10,
    value: initial.gearTier ?? 10,
    format: (v) => `T${v}`,
  });

  const gearIncludeCheck = el('input', {
    type: 'checkbox',
    checked: initial.gearIncludeExisting === true,
    style: { accentColor: 'var(--accent)', width: '15px', height: '15px', cursor: 'pointer' },
  });
  const gearIncludeRow = el(
    'div',
    {
      style: {
        display: 'grid',
        gridTemplateColumns: '15px 1fr',
        alignItems: 'center',
        columnGap: '10px',
        padding: '10px 0',
        borderTop: '1px solid rgba(255,255,255,.06)',
      },
    },
    gearIncludeCheck,
    el(
      'div',
      { style: { minWidth: '0' } },
      el('div', {
        text: 'Also overwrite gear on students that already have some',
        style: { fontSize: '13px', color: 'var(--ink-1)', fontWeight: '600' },
      }),
      el('div', {
        text: 'Values are written as-is: a lower tier or level downgrades that gear too.',
        style: { fontSize: '11.5px', color: 'var(--ink-3)', marginTop: '1px' },
      }),
    ),
  );

  const preview = el('div.mono', {
    'data-selectable': '',
    style: {
      marginTop: '14px',
      padding: '9px 11px',
      borderRadius: '8px',
      background: 'rgba(0,0,0,.3)',
      fontSize: '12px',
      color: 'var(--ink-3)',
      wordBreak: 'break-all',
      lineHeight: '1.5',
    },
  });

  const controls = { level, star, skill: skills, ex, potential, ue: weapon, gear };

  const values = () => ({
    level: level.get(),
    star: { ...star.get(), base: starBaseCheck.checked },
    skill: skills.get(),
    ex: ex.get(),
    potential: potential.get(),
    ue: weapon.get(),
    gear: { enabled: gear.get().enabled, level: gear.get().value, tier: gearTier.get(), includeExisting: gearIncludeCheck.checked },
  });

  const apply = button('Apply', { variant: 'primary', iconName: 'check' });
  const cancel = button('Cancel', { variant: 'ghost' });

  const refresh = () => {
    const v = values();

    // Level and tier are coupled, but the tier track stays full length so it can always be dragged.
    // Instead of truncating the track, the value is pulled back into the band the level allows; the
    // level itself only moves when the tier slider is the thing being dragged (its own listener).
    const tierMin = gearTierForLevel(v.gear.level);
    const tierMax = gearMaxTierForLevel(v.gear.level);
    const before = gearTier.get();
    if (before < tierMin) gearTier.setValue(tierMin);
    else if (before > tierMax) gearTier.setValue(tierMax);
    v.gear.tier = gearTier.get();

    const command = buildCommand(v);
    const nothing =
      !v.level.enabled && !v.star.enabled && !v.skill.enabled && !v.ex.enabled && !v.potential.enabled && !v.ue.enabled && !v.gear.enabled;
    apply.disabled = nothing;
    apply.style.opacity = nothing ? '.5' : '1';
    preview.textContent = nothing ? 'Nothing selected yet - tick a field to include it.' : command;

    const chosenTier = v.gear.tier;
    gear.setHint(
      `Lv ${v.gear.level} belongs to T${tierMin}; T${chosenTier} covers Lv ${GEAR_TIER_MIN_LEVEL[chosenTier]}-${GEAR_TIER_MAX_LEVEL[chosenTier]}`,
    );
    gearTier.setHint(`T${chosenTier} starts at Lv ${GEAR_TIER_MIN_LEVEL[chosenTier]}; this level allows T${tierMin}-T${tierMax}`);

    // Hints can depend on other controls: unique equipment depends on the star choice.
    if (hints) {
      const text = hints(v) || {};
      for (const key of Object.keys(text)) {
        if (controls[key]) controls[key].setHint(text[key]);
      }
    }
  };

  // Quick presets keep the old one-click actions without bringing back max-or-nothing buttons.
  const presetBar = presets.length
    ? el(
        'div',
        { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', padding: '2px 0 12px' } },
        ...presets.map((p) =>
          button(p.label, {
            variant: 'ghost',
            sm: true,
            onClick: () => {
              for (const [key, ctl] of Object.entries(controls)) {
                const spec = p.values[key];
                if (!spec) ctl.set(false, null);
                else ctl.set(true, spec.value ?? null);
              }
              gearIncludeCheck.checked = !!p.values.gearIncludeExisting;
              if (p.values.gearTier != null) gearTier.setValue(p.values.gearTier);
              starBaseCheck.checked = !!p.values.star?.base;
              if (p.values.star?.base) star.set(true, null);
              refresh();
            },
          }),
        ),
      )
    : null;

  const body = el(
    'div',
    { style: { minWidth: '0' } },
    subtitle ? el('div', { text: subtitle, style: { fontSize: '13px', color: 'var(--ink-3)', marginBottom: '8px' } }) : null,
    el('div', {
      text: 'Values are absolute: a lower value overwrites what the account has now. A database snapshot is taken first.',
      style: { fontSize: '12px', color: 'var(--ink-3)', marginBottom: '10px', opacity: '.85' },
    }),
    presetBar,
    level.row,
    star.row,
    starBaseRow,
    skills.row,
    ex.row,
    potential.row,
    weapon.row,
    gear.row,
    gearTier.row,
    gearIncludeRow,
    preview,
  );

  const ref = modal({ title, body, footer: [cancel, apply], wide: true });

  const tierRangeEl = gearTier.row.querySelector('input[type="range"]');
  tierRangeEl.addEventListener('input', () => {
    const tier = Number(tierRangeEl.value);
    level.set(true, GEAR_TIER_MIN_LEVEL[tier]);
    refresh();
  });

  body.addEventListener('input', refresh);
  body.addEventListener('change', refresh);
  refresh();

  cancel.addEventListener('click', () => ref.close());

  apply.addEventListener('click', async () => {
    const v = values();
    const command = buildCommand(v);
    if (!command) return;
    ref.close();
    await onApply(command, v);
  });
}
