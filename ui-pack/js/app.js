(() => {
  'use strict';

  const screens = [...document.querySelectorAll('[data-screen]')];
  const toast = document.querySelector('#toast');
  let toastTimer;
  let currentScreen = 'main-menu';

  const ROOMS = [
    {id:1,name:'Battlefield Alpha',map:'Archipelago',mode:'CONQUEST',players:12,capacity:32,ping:48,region:'EU',status:'OPEN'},
    {id:2,name:'Noob Friendly',map:'Island',mode:'DOMINATION',players:6,capacity:16,ping:62,region:'ASIA',status:'OPEN'},
    {id:3,name:'Hardcore Only',map:'Coastline',mode:'FRONTLINE',players:20,capacity:32,ping:120,region:'EU',status:'LOCKED'},
    {id:4,name:'Vietnam Mod',map:'Jungle',mode:'CONQUEST',players:8,capacity:24,ping:85,region:'ASIA',status:'OPEN'},
    {id:5,name:'Custom Weapons',map:'Dustbowl',mode:'DOMINATION',players:16,capacity:32,ping:73,region:'NA',status:'OPEN'},
    {id:6,name:'Vehicle Mayhem',map:'Valley',mode:'CONQUEST',players:24,capacity:32,ping:134,region:'EU',status:'OPEN'},
    {id:7,name:'Night Watch',map:'Citadel',mode:'FRONTLINE',players:16,capacity:16,ping:39,region:'EU',status:'FULL'}
  ];

  let lobbyState = {
    name:'Coastline Vanguard', map:'Archipelago', localTeam:'blue', localReady:false,
    blue:[
      {id:'local',name:'VANGUARD_07',level:18,ping:42,host:true,ready:false},
      {id:'b2',name:'NORTHSTAR',level:31,ping:55,ready:true},
      {id:'b3',name:'KILO_ACTUAL',level:12,ping:71,ready:false}
    ],
    orange:[
      {id:'o1',name:'RED_FOX',level:27,ping:38,ready:true},
      {id:'o2',name:'BRAVO_ZERO',level:9,ping:96,ready:false},
      {id:'o3',name:'MISTRAL',level:21,ping:64,ready:true}
    ]
  };

  const DEFAULT_SETTINGS = {
    resolution:'1920 × 1080', displayMode:'FULLSCREEN', fpsLimit:'120 FPS', quality:'HIGH',
    vsync:true, motionBlur:false, masterVolume:'85', musicVolume:'60', sfxVolume:'90', voiceVolume:'75',
    dynamicRange:'MEDIUM', outputDevice:'DEFAULT SYSTEM DEVICE', fov:'90', sensitivity:'50',
    language:'ENGLISH', colorblind:'OFF', subtitles:true, cameraShake:true
  };

  function showToast(message) {
    clearTimeout(toastTimer);
    toast.textContent = message;
    toast.classList.add('is-visible');
    toastTimer = setTimeout(() => toast.classList.remove('is-visible'), 2400);
  }

  function navigate(screenId) {
    const next = screens.find(screen => screen.dataset.screen === screenId);
    if (!next) return;
    screens.forEach(screen => {
      const active = screen === next;
      screen.hidden = !active;
      screen.classList.toggle('is-active', active);
    });
    currentScreen = screenId;
    if (screenId === 'rooms') renderRooms();
    if (screenId === 'waiting-room') renderLobby();
    if (screenId === 'settings') loadSettings();
    const heading = next.querySelector('h1, button, input');
    requestAnimationFrame(() => heading?.focus({preventScroll:true}));
  }

  function filterRooms() {
    const query = document.querySelector('#room-search').value.trim().toLowerCase();
    const mode = document.querySelector('#mode-filter').value;
    const region = document.querySelector('#region-filter').value;
    return ROOMS.filter(room => {
      const haystack = `${room.name} ${room.map} ${room.mode}`.toLowerCase();
      return (!query || haystack.includes(query)) && (mode === 'all' || room.mode === mode) && (region === 'all' || room.region === region);
    });
  }

  function renderRooms() {
    const list = document.querySelector('#room-list');
    if (!list) return;
    const visibleRooms = filterRooms();
    list.innerHTML = visibleRooms.map(room => {
      const pingClass = room.ping > 110 ? 'ping--high' : room.ping > 80 ? 'ping--mid' : '';
      const disabled = room.status === 'FULL' ? 'disabled' : '';
      return `<div class="room-row" role="row" data-room-id="${room.id}">
        <span class="room-name">${room.name}</span>
        <span class="room-map">${room.map}<small>${room.mode} • ${room.region}</small></span>
        <span class="players">${room.players} / ${room.capacity}</span>
        <span class="ping ${pingClass}"><i></i>${room.ping}</span>
        <span class="room-status ${room.status === 'LOCKED' ? 'room-status--locked' : ''}">${room.status}</span>
        <button class="join-button" ${disabled}>${room.status === 'FULL' ? 'FULL' : 'JOIN'}</button>
      </div>`;
    }).join('') || '<p style="padding:26px;color:#7896aa">No operations match these filters.</p>';
    document.querySelector('#room-count').textContent = visibleRooms.length;
    list.querySelectorAll('.join-button:not(:disabled)').forEach(button => button.addEventListener('click', () => {
      const room = ROOMS.find(item => item.id === Number(button.closest('.room-row').dataset.roomId));
      lobbyState.name = room.name;
      lobbyState.map = room.map;
      if (room.status === 'LOCKED') showToast('Secure room accepted — demo access granted');
      navigate('waiting-room');
    }));
  }

  function createRoom(form) {
    const data = new FormData(form);
    const name = String(data.get('roomName') || '').trim();
    const error = document.querySelector('[data-error="create-room"]');
    if (name.length < 3) {
      error.textContent = 'Room name must contain at least 3 characters.';
      return false;
    }
    if (data.get('private') && String(data.get('roomPassword') || '').length < 6) {
      error.textContent = 'Private operations require a password of at least 6 characters.';
      return false;
    }
    error.textContent = '';
    lobbyState.name = name;
    lobbyState.map = String(data.get('map'));
    document.querySelector('#lobby-name').textContent = name.toUpperCase();
    document.querySelector('#lobby-map').textContent = lobbyState.map.toUpperCase();
    navigate('waiting-room');
    showToast('Operation created — invite your squad');
    return true;
  }

  function playerCard(player) {
    const initials = player.name.split(/[_\s-]/).map(part => part[0]).join('').slice(0,2);
    return `<article class="player-card" data-player-id="${player.id}"><div class="player-avatar">${initials}</div><div class="player-info"><b>${player.name}${player.id === 'local' ? ' (YOU)' : ''}</b><small>LEVEL ${player.level} • ASSAULT</small></div><div class="player-meta">${player.host ? '<span class="mini-badge">HOST</span>' : ''}${player.ready ? '<span class="mini-badge mini-badge--ready">READY</span>' : ''}<span class="player-ping">${player.ping} ms</span></div></article>`;
  }

  function renderLobby() {
    document.querySelector('#lobby-name').textContent = lobbyState.name.toUpperCase();
    document.querySelector('#lobby-map').textContent = lobbyState.map.toUpperCase();
    document.querySelector('#team-blue').innerHTML = lobbyState.blue.map(playerCard).join('');
    document.querySelector('#team-orange').innerHTML = lobbyState.orange.map(playerCard).join('');
    document.querySelector('#blue-count').textContent = lobbyState.blue.length;
    document.querySelector('#orange-count').textContent = lobbyState.orange.length;
    const ready = document.querySelector('#ready-button');
    ready.textContent = lobbyState.localReady ? 'STAND DOWN' : 'READY UP';
    ready.classList.toggle('action--command', lobbyState.localReady);
    const allReady = [...lobbyState.blue, ...lobbyState.orange].filter(p => p.id !== 'local').every(p => p.ready);
    document.querySelector('#start-game').disabled = !(lobbyState.localReady && allReady);
  }

  function switchTeam() {
    const from = lobbyState.localTeam === 'blue' ? lobbyState.blue : lobbyState.orange;
    const to = lobbyState.localTeam === 'blue' ? lobbyState.orange : lobbyState.blue;
    const index = from.findIndex(player => player.id === 'local');
    if (index >= 0) to.push(from.splice(index, 1)[0]);
    lobbyState.localTeam = lobbyState.localTeam === 'blue' ? 'orange' : 'blue';
    renderLobby();
    showToast(`Switched to Team ${lobbyState.localTeam === 'blue' ? 'Blue' : 'Orange'}`);
  }

  function toggleReady() {
    lobbyState.localReady = !lobbyState.localReady;
    const local = [...lobbyState.blue, ...lobbyState.orange].find(player => player.id === 'local');
    local.ready = lobbyState.localReady;
    renderLobby();
    showToast(lobbyState.localReady ? 'Ready status confirmed' : 'Ready status cancelled');
  }

  async function copyInviteCode(code) {
    try {
      await navigator.clipboard.writeText(code);
      showToast(`Invite code ${code} copied`);
    } catch {
      showToast(`Invite code: ${code}`);
    }
  }

  function startPractice(form) {
    const data = new FormData(form);
    const config = {
      map:String(data.get('practiceMap')),
      mode:String(data.get('practiceMode')),
      difficulty:String(data.get('difficulty')),
      bots:Number(data.get('botCount')),
      matchTime:String(data.get('matchTime')),
      weather:String(data.get('weather')),
      timeOfDay:String(data.get('timeOfDay')),
      playerTeam:String(data.get('playerTeam')),
      vehicles:data.get('vehicles') === 'on',
      friendlyFire:data.get('friendlyFire') === 'on'
    };
    showToast(`Deploying solo to ${config.map} • ${config.bots} AI • ${config.difficulty}`);
    form.querySelector('[type="submit"]').textContent = 'LOADING SIMULATION...';
    setTimeout(() => {
      form.querySelector('[type="submit"]').textContent = 'START PRACTICE ›';
      showToast('Practice launch event dispatched to the game');
    }, 1500);
    return config;
  }

  function settingsToObject(form) {
    const data = new FormData(form);
    return {
      resolution:String(data.get('resolution')), displayMode:String(data.get('displayMode')),
      fpsLimit:String(data.get('fpsLimit')), quality:String(data.get('quality')),
      vsync:data.get('vsync') === 'on', motionBlur:data.get('motionBlur') === 'on',
      masterVolume:String(data.get('masterVolume')), musicVolume:String(data.get('musicVolume')),
      sfxVolume:String(data.get('sfxVolume')), voiceVolume:String(data.get('voiceVolume')),
      dynamicRange:String(data.get('dynamicRange')), outputDevice:String(data.get('outputDevice')),
      fov:String(data.get('fov')), sensitivity:String(data.get('sensitivity')),
      language:String(data.get('language')), colorblind:String(data.get('colorblind')),
      subtitles:data.get('subtitles') === 'on', cameraShake:data.get('cameraShake') === 'on'
    };
  }

  function applySettingsToForm(settings) {
    const form = document.querySelector('#settings-form');
    Object.entries(settings).forEach(([name,value]) => {
      const input = form.elements.namedItem(name);
      if (!input) return;
      if (input.type === 'checkbox') input.checked = Boolean(value);
      else input.value = value;
    });
    form.querySelectorAll('input[type="range"]').forEach(input => input.closest('label').querySelector('output').value = `${input.value}${input.name.includes('Volume') ? '%' : ''}`);
  }

  function loadSettings() {
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem('ironfront-settings') || '{}'); } catch { saved = {}; }
    applySettingsToForm({...DEFAULT_SETTINGS, ...saved});
    document.querySelector('#settings-status').textContent = 'SETTINGS LOADED';
    return {...DEFAULT_SETTINGS, ...saved};
  }

  function saveSettings(form) {
    const settings = settingsToObject(form);
    try { localStorage.setItem('ironfront-settings', JSON.stringify(settings)); } catch { /* local storage may be unavailable in restricted previews */ }
    document.querySelector('#settings-status').textContent = 'ALL CHANGES SAVED';
    showToast('Settings applied to local profile');
    return settings;
  }

  function resetSettings() {
    applySettingsToForm(DEFAULT_SETTINGS);
    try { localStorage.setItem('ironfront-settings', JSON.stringify(DEFAULT_SETTINGS)); } catch { /* keep the in-memory reset */ }
    document.querySelector('#settings-status').textContent = 'DEFAULTS RESTORED';
    showToast('Default settings restored');
    return {...DEFAULT_SETTINGS};
  }

  document.addEventListener('click', event => {
    const nav = event.target.closest('[data-nav]');
    const message = event.target.closest('[data-toast]');
    const toggle = event.target.closest('[data-toggle-password]');
    if (nav) navigate(nav.dataset.nav);
    if (message) showToast(message.dataset.toast);
    if (toggle) {
      const input = document.getElementById(toggle.dataset.togglePassword);
      input.type = input.type === 'password' ? 'text' : 'password';
      toggle.setAttribute('aria-label', input.type === 'password' ? 'Show password' : 'Hide password');
    }
  });

  document.querySelector('#signin-form').addEventListener('submit', event => {
    event.preventDefault();
    const form = event.currentTarget;
    const error = form.querySelector('[data-error="signin"]');
    if (!form.checkValidity()) {
      error.textContent = 'Enter a callsign and a password of at least 6 characters.';
      return;
    }
    error.textContent = '';
    navigate('rooms');
    showToast('Secure connection established');
  });

  document.querySelector('#register-form').addEventListener('submit', event => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const error = form.querySelector('[data-error="register"]');
    if (!form.checkValidity()) error.textContent = 'Username must be 3–16 lowercase letters, numbers or underscores; password must contain at least 8 characters.';
    else if (data.get('password') !== data.get('repeatPassword')) error.textContent = 'Passwords do not match.';
    else {
      error.textContent = '';
      navigate('sign-in');
      showToast('Operative created — sign in to deploy');
    }
  });

  document.querySelector('#room-search').addEventListener('input', renderRooms);
  document.querySelector('#mode-filter').addEventListener('change', renderRooms);
  document.querySelector('#region-filter').addEventListener('change', renderRooms);
  document.querySelector('#refresh-rooms').addEventListener('click', () => {
    document.querySelector('#refresh-time').textContent = 'updated just now';
    renderRooms();
    showToast('Server list refreshed');
  });
  document.querySelector('#quick-match').addEventListener('click', () => {
    const fastest = [...ROOMS].filter(r => r.status === 'OPEN' && r.players < r.capacity).sort((a,b) => a.ping - b.ping)[0];
    lobbyState.name = fastest.name;
    lobbyState.map = fastest.map;
    navigate('waiting-room');
    showToast(`Quick Match selected ${fastest.name}`);
  });

  const privateToggle = document.querySelector('#private-toggle');
  privateToggle.addEventListener('change', () => {
    const wrap = document.querySelector('#password-wrap');
    const input = wrap.querySelector('input');
    input.disabled = !privateToggle.checked;
    input.required = privateToggle.checked;
    wrap.classList.toggle('is-disabled', !privateToggle.checked);
  });
  document.querySelector('#create-room-form [name="map"]').addEventListener('change', event => {
    document.querySelector('#map-preview-name').textContent = event.target.value;
  });
  document.querySelector('#create-room-form').addEventListener('submit', event => {
    event.preventDefault();
    createRoom(event.currentTarget);
  });

  document.querySelector('#copy-code').addEventListener('click', () => copyInviteCode(document.querySelector('#invite-code').textContent));
  document.querySelector('#switch-team').addEventListener('click', switchTeam);
  document.querySelector('#ready-button').addEventListener('click', toggleReady);
  document.querySelector('#leave-room').addEventListener('click', () => { navigate('rooms'); showToast('You left the operation'); });
  document.querySelector('#start-game').addEventListener('click', () => showToast('Deployment sequence started'));

  document.querySelector('#practice-form').addEventListener('submit', event => {
    event.preventDefault();
    startPractice(event.currentTarget);
  });
  document.querySelector('#practice-form [name="practiceMap"]').addEventListener('change', event => {
    const descriptions = {
      ARCHIPELAGO:'Island chains, armored routes and naval approaches.',
      DUSTBOWL:'Open desert lanes with long-range vehicle combat.',
      COASTLINE:'Cliff roads, beachheads and fortified villages.',
      VALLEY:'Dense elevation changes and close infantry routes.',
      CITADEL:'Urban strongholds built for vertical engagements.'
    };
    document.querySelector('#practice-map-name').textContent = event.target.value;
    document.querySelector('#practice-map-description').textContent = descriptions[event.target.value];
  });
  document.querySelector('#practice-form [name="botCount"]').addEventListener('change', event => {
    document.querySelector('.practice-summary p b:nth-of-type(2)').textContent = `${event.target.value} AI UNITS`;
  });

  document.querySelectorAll('[data-settings-tab]').forEach(button => button.addEventListener('click', () => {
    document.querySelectorAll('[data-settings-tab]').forEach(item => item.classList.toggle('is-active', item === button));
    document.querySelectorAll('[data-settings-group]').forEach(group => group.classList.toggle('is-active', group.dataset.settingsGroup === button.dataset.settingsTab));
  }));
  document.querySelectorAll('#settings-form input[type="range"]').forEach(input => input.addEventListener('input', () => {
    input.closest('label').querySelector('output').value = `${input.value}${input.name.includes('Volume') ? '%' : ''}`;
    document.querySelector('#settings-status').textContent = 'UNSAVED CHANGES';
  }));
  document.querySelector('#settings-form').addEventListener('change', () => document.querySelector('#settings-status').textContent = 'UNSAVED CHANGES');
  document.querySelector('#settings-form').addEventListener('submit', event => {
    event.preventDefault();
    saveSettings(event.currentTarget);
  });
  document.querySelector('#reset-settings').addEventListener('click', resetSettings);

  renderRooms();
  window.IRONFRONT = { screens:screens.map(s => s.dataset.screen), navigate, filterRooms, createRoom, switchTeam, toggleReady, copyInviteCode, startPractice, saveSettings, resetSettings, loadSettings, getCurrentScreen:() => currentScreen };
})();
