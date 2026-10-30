<script>
  import { settings, persistSettings } from './lib/settings.svelte.js';
  import { BOARD_THEMES } from './lib/boardTheme.js';
  import { authState, clearAuth }      from './lib/auth.svelte.js';
  import { navigate }                  from './lib/navigate.js';
  import { currentGame }               from './lib/currentGame.svelte.js';
  import { setVolume }                 from './lib/sound.js';

  let settingsOpen  = $state(false);
  let menuOpen      = $state(false);
  let userMenuOpen  = $state(false);

  const isLoggedIn = $derived(!!authState.token);
  const userName   = $derived(
    authState.user
      ? (authState.user.displayName ?? authState.user.username)
      : null
  );

  function toggleSettings() { settingsOpen = !settingsOpen; userMenuOpen = false; }
  function toggleMenu()     { menuOpen = !menuOpen; }
  function toggleUserMenu() { userMenuOpen = !userMenuOpen; settingsOpen = false; }

  function closeDropdowns(e) {
    if (!e.target.closest('.settings-wrap')) settingsOpen = false;
    if (!e.target.closest('.nav-left'))      menuOpen     = false;
    if (!e.target.closest('.user-menu-wrap')) userMenuOpen = false;
  }

  function toggleCoords() {
    settings.showCoordinates = !settings.showCoordinates;
    persistSettings();
  }

  function handleVolumeChange(e) {
    settings.volume = parseInt(e.target.value, 10);
    setVolume(settings.volume / 100);  // Convert 0-100 to 0.0-1.0
    persistSettings();
  }

  // Initialize volume on load
  $effect(() => {
    setVolume(settings.volume / 100);
  });

  let seedCopied = $state(false);
  let _seedCopiedTimer = null;
  function copyMapSeed() {
    if (!currentGame.mapSeed) return;
    navigator.clipboard.writeText(currentGame.mapSeed).catch(() => {});
    seedCopied = true;
    clearTimeout(_seedCopiedTimer);
    _seedCopiedTimer = setTimeout(() => { seedCopied = false; }, 2000);
  }

  function handleLogout() {
    userMenuOpen = false;
    clearAuth();
    navigate('/');
  }

  function navTo(path) {
    menuOpen = false;
    navigate(path);
  }

  function userNavTo(path) {
    userMenuOpen = false;
    navigate(path);
  }
</script>

<svelte:window onclick={closeDropdowns} />

<nav class="navbar">
  <!-- Hamburger: mobile only -->
  <div class="nav-left">
    <button class="hamburger-btn" onclick={toggleMenu} aria-label="Menu">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6.5h16M4 12h16M4 17.5h16" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" fill="none"/></svg>
    </button>
    {#if menuOpen}
      <div class="mobile-menu">
        <button class="mobile-menu-item" onclick={() => navTo('/')}>Play</button>
        <button class="mobile-menu-item" onclick={() => navTo('/watch')}>Watch</button>
        <button class="mobile-menu-item" onclick={() => navTo('/analyze')}>Analyze</button>
        <button class="mobile-menu-item sub-item" onclick={() => navTo('/library')}>My library</button>
        <button class="mobile-menu-item" onclick={() => navTo('/rules')}>Rules</button>
        <button class="mobile-menu-item" onclick={() => navTo('/about')}>About</button>
      </div>
    {/if}
  </div>

  <button class="logo-btn" onclick={() => navigate('/')}>
    <img src="/assets/other/logo.svg" alt="" class="logo-img" />
    <span class="logo-text"><span class="logo-gold">Goldrush</span> Gambit</span>
  </button>

  <!-- Desktop nav links: hidden on mobile -->
  <div class="nav-center">
    <button class="nav-link" onclick={() => navigate('/')}>Play</button>
    <button class="nav-link" onclick={() => navigate('/watch')}>Watch</button>
    <!-- Analyze opens the analysis board; hovering it shows its submenu -->
    <div class="nav-group">
      <button class="nav-link" onclick={() => navigate('/analyze')}>Analyze</button>
      <div class="submenu">
        <div class="submenu-box">
          <button class="dropdown-btn dropdown-item" onclick={() => navigate('/library')}>
            My library
          </button>
        </div>
      </div>
    </div>
    <button class="nav-link" onclick={() => navigate('/rules')}>Rules</button>
    <button class="nav-link" onclick={() => navigate('/about')}>About</button>
  </div>

  <div class="nav-right">
    <div class="settings-wrap">
      <button class="icon-btn" title="Settings" aria-label="Settings" onclick={toggleSettings}>
        <svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" fill-rule="evenodd" d="M9.97 3.85 L10.36 1.12 L13.64 1.12 L14.03 3.85 L16.33 4.80 L18.54 3.15 L20.85 5.46 L19.20 7.67 L20.15 9.97 L22.88 10.36 L22.88 13.64 L20.15 14.03 L19.20 16.33 L20.85 18.54 L18.54 20.85 L16.33 19.20 L14.03 20.15 L13.64 22.88 L10.36 22.88 L9.97 20.15 L7.67 19.20 L5.46 20.85 L3.15 18.54 L4.80 16.33 L3.85 14.03 L1.12 13.64 L1.12 10.36 L3.85 9.97 L4.80 7.67 L3.15 5.46 L5.46 3.15 L7.67 4.80 Z M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z"/></svg>
      </button>
      {#if settingsOpen}
        <div class="dropdown settings-dropdown">
          <label class="dropdown-item toggle-item">
            Show coordinates
            <input type="checkbox" checked={settings.showCoordinates} onchange={toggleCoords} />
          </label>
          <div class="dropdown-item theme-item">
            <label for="board-theme" class="volume-label">Board</label>
            <select
              id="board-theme"
              class="theme-select"
              value={settings.boardTheme}
              onchange={(e) => { settings.boardTheme = e.currentTarget.value; persistSettings(); }}
            >
              {#each BOARD_THEMES as t}
                <option value={t.id}>{t.name}</option>
              {/each}
            </select>
          </div>
          <div class="dropdown-item volume-item">
            <div class="volume-label">
              Volume
            </div>
            <input
              type="range"
              min="0"
              max="100"
              value={settings.volume}
              oninput={handleVolumeChange}
              class="volume-slider"
            />
          </div>
          <button
            class="dropdown-btn dropdown-item"
            disabled={!currentGame.mapSeed}
            onclick={copyMapSeed}
          >
            {seedCopied ? 'Copied!' : 'Copy board seed'}
          </button>
        </div>
      {/if}
    </div>

    {#if isLoggedIn}
      <div class="user-menu-wrap">
        <button class="user-menu-btn" onclick={toggleUserMenu}>
          <span class="user-menu-name">{userName}</span>
          <span class="user-menu-arrow" class:open={userMenuOpen}>▾</span>
        </button>
        {#if userMenuOpen}
          <div class="dropdown user-dropdown">
            <button class="dropdown-btn dropdown-item" onclick={() => userNavTo(`/profile/${authState.user?.username}`)}>
              Profile
            </button>
            <button class="dropdown-btn dropdown-item" onclick={() => userNavTo('/settings')}>
              Account settings
            </button>
            <div class="dropdown-divider"></div>
            <button class="dropdown-btn dropdown-item logout-item" onclick={handleLogout}>
              Log out
            </button>
          </div>
        {/if}
      </div>
    {:else}
      <button class="nav-btn" onclick={() => navigate('/register')}>Sign up</button>
      <button class="nav-btn" onclick={() => navigate('/login')}>Sign in</button>
    {/if}
  </div>
</nav>

<style>
  .navbar {
    position: sticky;
    top: 0;
    z-index: 200;
    display: flex;
    align-items: center;
    padding: 0 1rem;
    height: 46px;
    background: #15130f;
    border-bottom: 1px solid var(--line);
    flex-shrink: 0;
  }

  /* ── Hamburger (mobile only) ── */
  .nav-left {
    display: none;
    align-items: center;
    position: relative;
    margin-right: 0.5rem;
  }
  /* Square icon buttons with SVG icons, so the icon is exactly centred */
  .hamburger-btn, .icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 34px;
    height: 34px;
    padding: 0;
    background: none;
    border: none;
    color: var(--muted);
    border-radius: 4px;
  }
  .hamburger-btn svg { width: 22px; height: 22px; display: block; }
  .icon-btn svg { width: 22px; height: 22px; display: block; }
  .hamburger-btn:hover { background: var(--line); color: var(--fg); }

  .mobile-menu {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    background: var(--surface);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 0.4rem 0;
    min-width: 130px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.5);
    z-index: 300;
  }
  .mobile-menu-item {
    display: block;
    width: 100%;
    background: none;
    border: none;
    text-align: left;
    font-family: inherit;
    font-size: 1.08rem;
    color: var(--fg);
    padding: 0.55rem 1rem;
  }
  .mobile-menu-item:hover { background: var(--line); }
  .mobile-menu-item.sub-item { padding-left: 2rem; font-size: 1rem; color: var(--fg-2); }

  /* ── Desktop nav links ── */
  .nav-center {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    margin-left: 1.5rem;
  }
  .nav-link {
    background: none;
    border: none;
    color: var(--fg-2);
    font-size: 1.08rem;
    padding: 0.25rem 0.7rem;
    border-radius: 4px;
  }
  .nav-link:hover { background: var(--line); color: var(--fg); }

  /* Nav item with a hover submenu. The submenu's top padding bridges the gap below the link,
     so the pointer can move down into it without the menu closing. */
  .nav-group { position: relative; }
  .submenu {
    display: none;
    position: absolute;
    left: 0;
    top: 100%;
    padding-top: 6px;
    z-index: 300;
  }
  .nav-group:hover .submenu,
  .nav-group:has(:focus-visible) .submenu { display: block; }   /* keyboard; not after a mouse click */
  .nav-group:hover > .nav-link { background: var(--line); color: var(--fg); }
  .submenu-box {
    background: var(--surface);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 0.4rem 0;
    min-width: 160px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.5);
  }
  .submenu .dropdown-item { padding: 0.55rem 1rem; font-size: 1rem; white-space: nowrap; }

  .logo-btn {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    background: none;
    border: none;
    color: var(--fg);
    font-size: 1.15rem;
    font-weight: 700;
    padding: 0;
    cursor: pointer;
    white-space: nowrap;
  }
  .logo-btn:hover { color: var(--accent-2); }
  .logo-text {
    font-family: var(--font-display);
    font-size: 1.3rem;
    letter-spacing: 0.02em;
    /* Flex centring uses the line box, which reserves descender space small caps don't use */
    transform: translateY(-0.05em);
  }
  .logo-gold {
    color: #d4a017;
  }
  .logo-btn:hover .logo-gold {
    color: #e8b820;
  }
  .logo-img {
    width: 28px;
    height: 28px;
    flex-shrink: 0;
  }

  .nav-right {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-left: auto;
  }

  .settings-wrap { position: relative; }

  .icon-btn:hover { background: var(--line); color: var(--fg); }

  .dropdown {
    position: absolute;
    right: 0;
    top: calc(100% + 6px);
    background: var(--surface);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 0.4rem 0;
    min-width: 190px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.5);
    z-index: 300;
  }

  .dropdown-item {
    display: flex;
    align-items: center;
    gap: 0.55rem;
    padding: 0.45rem 0.85rem;
    font-size: 0.88rem;
    color: var(--fg);
    cursor: pointer;
    user-select: none;
  }
  .dropdown-item:hover { background: var(--line); }

  /* Settings menu: roomier than the user menu */
  .settings-dropdown { padding: 0.6rem 0; min-width: 240px; }
  .settings-dropdown .dropdown-item { padding: 0.7rem 1.1rem; gap: 0.9rem; }
  .toggle-item { justify-content: space-between; }

  .theme-item { cursor: default; justify-content: space-between; }
  .theme-item:hover { background: none; }
  .theme-select {
    background: var(--line);
    border: 1px solid var(--line-strong);
    border-radius: 4px;
    color: var(--fg);
    font-size: 0.85rem;
    padding: 0.2rem 0.35rem;
  }

  .volume-item {
    flex-direction: row;
    align-items: center;
    gap: 0.7rem;
    cursor: default;
  }
  .volume-item:hover { background: none; }

  .volume-label {
    font-size: 0.88rem;
    color: var(--fg);
  }

  .volume-slider {
    width: 100%;
    height: 4px;
    border-radius: 2px;
    background: var(--line-strong);
    outline: none;
    appearance: none;
    -webkit-appearance: none;
  }

  .volume-slider::-webkit-slider-thumb {
    appearance: none;
    -webkit-appearance: none;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    background: var(--accent-2);
    cursor: pointer;
  }

  .volume-slider::-webkit-slider-thumb:hover {
    background: var(--accent-2);
  }

  .volume-slider::-moz-range-thumb {
    width: 14px;
    height: 14px;
    border: none;
    border-radius: 50%;
    background: var(--accent-2);
    cursor: pointer;
  }

  .volume-slider::-moz-range-thumb:hover {
    background: var(--accent-2);
  }

  .dropdown-btn {
    width: 100%;
    background: none;
    border: none;
    text-align: left;
    font-family: inherit;
    font-size: 0.88rem;
    color: var(--fg);
    padding: 0.45rem 0.85rem;
  }

  /* ── User menu ── */
  .user-menu-wrap { position: relative; }

  .user-menu-btn {
    display: flex;
    align-items: center;
    gap: 0.3rem;
    background: none;
    border: none;
    color: var(--fg-2);
    font-size: 0.88rem;
    font-family: inherit;
    padding: 0.25rem 0.5rem;
    border-radius: 5px;
    max-width: 180px;
  }
  .user-menu-btn:hover { background: var(--line); color: var(--fg); }

  .user-menu-name {
    max-width: 130px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .user-menu-arrow {
    font-size: 1rem;
    color: var(--faint);
    transition: transform 0.15s;
    flex-shrink: 0;
  }
  .user-menu-arrow.open { transform: rotate(180deg); }

  .user-dropdown { right: 0; min-width: 170px; }

  .dropdown-divider {
    margin: 0.3rem 0;
    border-top: 1px solid var(--line);
  }

  .logout-item { color: #e07070 !important; }
  .logout-item:hover { background: #2a1a1a !important; }

  .dropdown-btn:disabled { opacity: 0.4; cursor: default; }
  .dropdown-btn:disabled:hover { background: none; }

  .nav-btn {
    padding: 0.3rem 0.8rem;
    background: var(--surface-2);
    border: 1px solid var(--line-strong);
    color: var(--fg-2);
    border-radius: 5px;
    font-size: 0.85rem;
    white-space: nowrap;
  }
  .nav-btn:hover { background: var(--surface-3); color: var(--fg); }

  /* ── Mobile breakpoint ── */
  @media (max-width: 640px) {
    .nav-left    { display: flex; }
    .nav-center  { display: none; }
    .logo-text   { display: none; }
  }
</style>
