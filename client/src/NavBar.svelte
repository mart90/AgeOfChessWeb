<script>
  import { settings, persistSettings } from './lib/settings.svelte.js';
  import { authState, clearAuth }      from './lib/auth.svelte.js';
  import { navigate }                  from './lib/navigate.js';
  import { currentGame }               from './lib/currentGame.svelte.js';

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
    <button class="hamburger-btn" onclick={toggleMenu} aria-label="Menu">☰</button>
    {#if menuOpen}
      <div class="mobile-menu">
        <button class="mobile-menu-item" onclick={() => navTo('/')}>Play</button>
        <button class="mobile-menu-item" onclick={() => navTo('/watch')}>Watch</button>
        <button class="mobile-menu-item" onclick={() => navTo('/sandbox')}>Sandbox</button>
        <button class="mobile-menu-item" onclick={() => navTo('/tutorial')}>Rules</button>
      </div>
    {/if}
  </div>

  <button class="logo-btn" onclick={() => navigate('/')}>
    <img src="/assets/other/logo.png" alt="" class="logo-img" />
    <span class="logo-text"><span class="logo-gold">Gold</span>rushGambit</span>
  </button>

  <!-- Desktop nav links: hidden on mobile -->
  <div class="nav-center">
    <button class="nav-link" onclick={() => navigate('/')}>Play</button>
    <button class="nav-link" onclick={() => navigate('/watch')}>Watch</button>
    <button class="nav-link" onclick={() => navigate('/sandbox')}>Sandbox</button>
    <button class="nav-link" onclick={() => navigate('/tutorial')}>Rules</button>
  </div>

  <div class="nav-right">
    <div class="settings-wrap">
      <button class="icon-btn" title="Settings" onclick={toggleSettings}>⚙</button>
      {#if settingsOpen}
        <div class="dropdown">
          <label class="dropdown-item">
            <input type="checkbox" checked={settings.showCoordinates} onchange={toggleCoords} />
            Show coordinates
          </label>
          <button
            class="dropdown-btn dropdown-item"
            disabled={!currentGame.mapSeed}
            onclick={copyMapSeed}
          >
            {seedCopied ? 'Copied!' : 'Copy map seed'}
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
      <button class="nav-btn" onclick={() => navigate('/register')}>Register</button>
      <button class="nav-btn" onclick={() => navigate('/login')}>Log in</button>
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
    background: #12122a;
    border-bottom: 1px solid #2a2a4a;
    flex-shrink: 0;
  }

  /* ── Hamburger (mobile only) ── */
  .nav-left {
    display: none;
    align-items: center;
    position: relative;
    margin-right: 0.5rem;
  }
  .hamburger-btn {
    background: none;
    border: none;
    color: #aaa;
    font-size: 1.2rem;
    padding: 0.2rem 0.4rem;
    border-radius: 4px;
    line-height: 1;
  }
  .hamburger-btn:hover { background: #2a2a4a; color: #eee; }

  .mobile-menu {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    background: #1e1e38;
    border: 1px solid #3a3a5a;
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
    font-size: 0.9rem;
    color: #ddd;
    padding: 0.5rem 0.9rem;
  }
  .mobile-menu-item:hover { background: #2a2a4a; }

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
    color: #aaa;
    font-size: 0.9rem;
    padding: 0.25rem 0.65rem;
    border-radius: 4px;
  }
  .nav-link:hover { background: #2a2a4a; color: #eee; }

  .logo-btn {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    background: none;
    border: none;
    color: #9aaae8;
    font-size: 1rem;
    font-weight: 700;
    padding: 0;
    cursor: pointer;
    white-space: nowrap;
  }
  .logo-btn:hover { color: #b8c5ff; }
  .logo-text {
    font-family: 'Cinzel Decorative', serif;
    letter-spacing: 0.01em;
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
    image-rendering: pixelated;
    flex-shrink: 0;
  }

  .nav-right {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-left: auto;
  }

  .settings-wrap { position: relative; }

  .icon-btn {
    background: none;
    border: none;
    color: #aaa;
    font-size: 1.15rem;
    padding: 0.2rem 0.35rem;
    border-radius: 4px;
    line-height: 1;
  }
  .icon-btn:hover { background: #2a2a4a; color: #eee; }

  .dropdown {
    position: absolute;
    right: 0;
    top: calc(100% + 6px);
    background: #1e1e38;
    border: 1px solid #3a3a5a;
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
    color: #ddd;
    cursor: pointer;
    user-select: none;
  }
  .dropdown-item:hover { background: #2a2a4a; }

  .dropdown-btn {
    width: 100%;
    background: none;
    border: none;
    text-align: left;
    font-family: inherit;
    font-size: 0.88rem;
    color: #ddd;
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
    color: #ccc;
    font-size: 0.88rem;
    font-family: inherit;
    padding: 0.25rem 0.5rem;
    border-radius: 5px;
    max-width: 180px;
  }
  .user-menu-btn:hover { background: #2a2a4a; color: #eee; }

  .user-menu-name {
    max-width: 130px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .user-menu-arrow {
    font-size: 1rem;
    color: #666;
    transition: transform 0.15s;
    flex-shrink: 0;
  }
  .user-menu-arrow.open { transform: rotate(180deg); }

  .user-dropdown { right: 0; min-width: 170px; }

  .dropdown-divider {
    margin: 0.3rem 0;
    border-top: 1px solid #2a2a4a;
  }

  .logout-item { color: #e07070 !important; }
  .logout-item:hover { background: #2a1a1a !important; }

  .dropdown-btn:disabled { opacity: 0.4; cursor: default; }
  .dropdown-btn:disabled:hover { background: none; }

  .nav-btn {
    padding: 0.3rem 0.8rem;
    background: #2d2d4a;
    border: 1px solid #3a3a5a;
    color: #ccc;
    border-radius: 5px;
    font-size: 0.85rem;
  }
  .nav-btn:hover { background: #3a3a6a; color: #eee; }

  /* ── Mobile breakpoint ── */
  @media (max-width: 640px) {
    .nav-left    { display: flex; }
    .nav-center  { display: none; }
    .logo-text   { display: none; }
  }
</style>
