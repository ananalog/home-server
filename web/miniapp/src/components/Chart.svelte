<script lang="ts">
  import uPlot from 'uplot';
  import { api } from '../lib/api';
  import type { Point } from '../lib/types';

  let { deviceId, point }: { deviceId: string; point: Point } = $props();
  let el: HTMLDivElement;
  let range = $state(24);
  let empty = $state(false);
  let plot: uPlot | null = null;

  const css = (v: string) => getComputedStyle(document.documentElement).getPropertyValue(v).trim() || '#888';

  async function load() {
    const to = Date.now();
    const from = to - range * 3600_000;
    const h = await api.history(deviceId, point.key, from, to);
    empty = h.points.length === 0;
    const xs = h.points.map((p) => p.t / 1000);
    const avg = h.points.map((p) => p.avg ?? null);
    const min = h.points.map((p) => p.min ?? null);
    const max = h.points.map((p) => p.max ?? null);
    plot?.destroy();
    const accent = css('--accent');
    const hint = css('--hint');
    const opts: uPlot.Options = {
      width: el.clientWidth,
      height: 200,
      legend: { show: false },
      cursor: { drag: { x: false, y: false } },
      scales: { x: { time: true, range: [from / 1000, to / 1000] } },
      axes: [
        {
          stroke: hint,
          grid: { stroke: 'rgba(128,128,128,0.12)' },
          ticks: { show: false },
          values: (_u, splits) =>
            splits.map((s) => {
              const d = new Date(s * 1000);
              return range <= 24
                ? d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })
                : d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
            }),
        },
        { stroke: hint, grid: { stroke: 'rgba(128,128,128,0.12)' }, ticks: { show: false }, size: 44 },
      ],
      series: [
        {},
        { stroke: 'transparent', points: { show: false } },
        { stroke: 'transparent', points: { show: false } },
        { stroke: accent, width: 2, points: { show: xs.length < 20, size: 5, fill: accent } },
      ],
      bands: [{ series: [2, 1], fill: 'rgba(128,128,128,0.15)' }],
    };
    plot = new uPlot(opts, [xs, min, max, avg], el);
  }

  $effect(() => {
    range;
    load().catch(() => (empty = true));
    return () => plot?.destroy();
  });
</script>

<div class="chart">
  <div class="chips">
    {#each [[24, '24 ч'], [168, '7 дн'], [720, '30 дн']] as [h, t]}
      <button class="chip" class:active={range === h} onclick={() => (range = h as number)}>{t}</button>
    {/each}
  </div>
  <div bind:this={el}></div>
  {#if empty}<div class="empty small">Нет данных за период</div>{/if}
</div>

<style>
  .chart {
    padding: 12px 8px 4px;
  }
  .chip:not(.active) {
    background: color-mix(in srgb, var(--hint) 12%, transparent);
    font-size: 13px;
    padding: 5px 12px;
  }
</style>
