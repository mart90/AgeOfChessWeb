<script>
  /**
   * A hover tooltip. The trigger is a small "?" icon unless a `trigger` snippet is given;
   * `children` is the tooltip content.
   * align: 'center' (tooltip centred on the trigger), 'start' (extending right, for left-hand labels)
   *        or 'end' (extending left from the trigger's right edge).
   * below: open below the trigger instead of above (when there's no room above).
   * enabled: false shows just the trigger, without a tooltip.
   */
  let { align = 'center', below = false, enabled = true, label = 'More information', trigger, children } = $props();
</script>

<span class="info-tip" class:start={align === 'start'} class:end={align === 'end'} class:below class:has-trigger={!!trigger}>
  {#if trigger}
    {@render trigger()}
  {:else}
    <!-- SVG rather than a "?" character, which sits off-centre in the site's font -->
    <button type="button" class="info-btn" aria-label={label}>
      <svg viewBox="0 0 16 16" aria-hidden="true">
        <circle class="ring" cx="8" cy="8" r="7.25" />
        <path class="mark" d="M6.1 6.2a1.9 1.9 0 1 1 2.75 1.7c-.5.25-.85.65-.85 1.2v.45" />
        <circle class="dot" cx="8" cy="11.55" r="0.85" />
      </svg>
    </button>
  {/if}
  {#if enabled}
    <span class="tip" role="tooltip">{@render children()}</span>
  {/if}
</span>

<style>
  .info-tip {
    position: relative;
    display: inline-flex;
    align-items: center;
    vertical-align: middle;
  }
  .info-tip.has-trigger { display: flex; flex: 1; }   /* e.g. wrapping a whole settings row */
  .info-btn {
    width: 1.05rem; height: 1.05rem;
    padding: 0;
    background: none;
    border: none;
    cursor: help;
    display: inline-flex;
    flex-shrink: 0;
  }
  .info-btn svg { width: 100%; height: 100%; display: block; overflow: visible; }
  .ring { fill: none; stroke: var(--faint); stroke-width: 1; }
  .mark { fill: none; stroke: var(--fg-2); stroke-width: 1.5; stroke-linecap: round; stroke-linejoin: round; }
  .dot  { fill: var(--fg-2); }
  .info-btn:hover .ring { stroke: var(--muted); }
  .tip {
    display: none;
    position: absolute;
    bottom: calc(100% + 8px);
    left: 50%;
    transform: translateX(-50%);
    width: max-content;
    max-width: min(270px, calc(100vw - 2rem));
    background: var(--bg);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 0.6rem 0.75rem;
    font-size: 0.8rem;
    font-weight: normal;
    font-style: normal;
    color: var(--muted);
    line-height: 1.55;
    white-space: normal;
    text-align: left;
    cursor: default;
    z-index: 100;
    pointer-events: none;
  }
  .start .tip { left: -0.5rem; transform: none; }
  .end .tip   { left: auto; right: -0.5rem; transform: none; }
  .below .tip { bottom: auto; top: calc(100% + 8px); }
  .tip :global(strong) { color: var(--fg-2); }
  .info-tip:hover .tip,
  .info-tip:focus-within .tip { display: block; }
</style>
