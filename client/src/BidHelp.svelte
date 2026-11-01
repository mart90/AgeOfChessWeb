<script>
  import InfoTip from './InfoTip.svelte';

  /** "?" explaining bidding for colors (props: see InfoTip). */
  let { align = 'center', below = false } = $props();
</script>

<InfoTip {align} {below} label="How bidding works">
  Both players secretly bid gold for the right to play White. The higher bidder
  wins White and their bid becomes Black's starting gold.
  <br><br>Bids can be negative when Black is better, which can happen on full random maps. In that case, Black starts with negative gold.
  <br><br>
  <strong>Example:</strong> You bid 8g, the opponent bids 6g. You play White, and the opponent plays Black with 8 starting gold.
</InfoTip>
