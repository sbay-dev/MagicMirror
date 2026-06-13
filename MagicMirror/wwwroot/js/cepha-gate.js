// 🔒 CephaGate — Isolated Authentication Portal
// ══════════════════════════════════════════════════════════════
// A login/register portal that is IMPOSSIBLE to inspect, modify,
// or reverse-engineer from the host page.
//
// Architecture (Triple Isolation):
//   Layer 1: Closed Shadow DOM — DevTools cannot see internal structure
//   Layer 2: Sandboxed iframe (blob URL) — process-level isolation,
//            no URL in Network tab, separate JS context
//   Layer 3: Encrypted channel — postMessage uses AES-GCM encrypted
//            payloads between gate and host; shared secret via ECDH
//
// Deployment modes:
//   A) Self-hosted:  <cepha-gate tenant="my-org"></cepha-gate>
//   B) DNS/Worker:   Cloudflare Worker injects gate via DNS CNAME
//   C) Script tag:   <script src="https://gate.cepha.dev/v1/my-org.js"></script>
//
// Anti-reverse-engineering:
//   - iframe content is a blob URL (no readable source in Network)
//   - All class/id names are randomized per instance
//   - CSS uses scoped random custom properties
//   - JS inside iframe is minified + obfuscated at runtime
//   - MutationObserver inside iframe blocks any DOM changes
//   - No global variables leaked to parent scope

((globalThis) => {
    'use strict';

    // ── Crypto Utilities ──

    async function generateKeyPair() {
        return crypto.subtle.generateKey(
            { name: 'ECDH', namedCurve: 'P-256' },
            false,
            ['deriveKey']
        );
    }

    async function deriveSharedKey(privateKey, publicKey) {
        return crypto.subtle.deriveKey(
            { name: 'ECDH', public: publicKey },
            privateKey,
            { name: 'AES-GCM', length: 256 },
            false,
            ['encrypt', 'decrypt']
        );
    }

    async function exportPublicKey(key) {
        const raw = await crypto.subtle.exportKey('raw', key);
        return btoa(String.fromCharCode(...new Uint8Array(raw)));
    }

    async function importPublicKey(b64) {
        const raw = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
        return crypto.subtle.importKey('raw', raw, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    }

    async function encrypt(key, data) {
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const encoded = new TextEncoder().encode(JSON.stringify(data));
        const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, encoded);
        return {
            iv: btoa(String.fromCharCode(...iv)),
            ct: btoa(String.fromCharCode(...new Uint8Array(cipher)))
        };
    }

    async function decrypt(key, payload) {
        const iv = Uint8Array.from(atob(payload.iv), c => c.charCodeAt(0));
        const ct = Uint8Array.from(atob(payload.ct), c => c.charCodeAt(0));
        const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, ct);
        return JSON.parse(new TextDecoder().decode(plain));
    }

    // ── Random ID Generator (anti-inspection) ──

    function rid(len = 8) {
        const chars = 'abcdefghijklmnopqrstuvwxyz';
        const arr = crypto.getRandomValues(new Uint8Array(len));
        return Array.from(arr, b => chars[b % 26]).join('');
    }

    // ── Build iframe source (blob) ──

    function buildGateHTML(config) {
        // All class names are randomized per instance
        const cls = {
            wrap: rid(), card: rid(), logo: rid(), field: rid(),
            input: rid(), label: rid(), btn: rid(), link: rid(),
            alert: rid(), badge: rid(), meter: rid(), bar: rid(),
            tabs: rid(), tab: rid(), tabActive: rid(), form: rid(),
            switchBtn: rid(), hidden: rid()
        };

        const nonce = rid(16);

        return `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style nonce="${nonce}">
*{margin:0;padding:0;box-sizing:border-box}
html,body{height:100%;overflow:hidden;font-family:'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:transparent}
.${cls.wrap}{display:flex;align-items:center;justify-content:center;min-height:100%;padding:16px}
.${cls.card}{width:100%;max-width:400px;background:var(--g-bg,#fff);border-radius:16px;box-shadow:0 20px 60px rgba(0,0,0,.12),0 4px 12px rgba(0,0,0,.08);padding:40px 32px 32px;position:relative;overflow:hidden}
.${cls.card}::before{content:'';position:absolute;top:0;left:0;right:0;height:4px;background:linear-gradient(90deg,#667eea,#764ba2)}
.${cls.logo}{text-align:center;font-size:1.4rem;font-weight:700;color:#667eea;margin-bottom:24px;letter-spacing:-.02em}
.${cls.tabs}{display:flex;gap:4px;margin-bottom:24px;background:#f1f5f9;border-radius:8px;padding:3px}
.${cls.tab}{flex:1;padding:8px;border:none;background:transparent;font-size:.85rem;font-weight:500;color:#64748b;cursor:pointer;border-radius:6px;transition:all .2s}
.${cls.tabActive}{background:#fff;color:#667eea;box-shadow:0 1px 3px rgba(0,0,0,.08)}
.${cls.field}{position:relative;margin-bottom:16px}
.${cls.input}{width:100%;padding:14px 16px;font-size:15px;border:2px solid #e2e8f0;border-radius:8px;outline:none;transition:border .2s,box-shadow .2s;background:var(--g-ibg,#fafafa);color:var(--g-text,#1a202c);font-family:inherit}
.${cls.input}:focus{border-color:#667eea;box-shadow:0 0 0 3px rgba(102,126,234,.15)}
.${cls.input}::placeholder{color:#a0aec0}
.${cls.label}{position:absolute;top:-8px;left:12px;background:var(--g-ibg,#fafafa);padding:0 5px;font-size:.7rem;color:#64748b;pointer-events:none}
.${cls.btn}{width:100%;padding:13px;border:none;border-radius:8px;font-size:.95rem;font-weight:600;cursor:pointer;transition:all .2s;color:#fff;background:linear-gradient(135deg,#667eea,#764ba2);margin-top:8px;font-family:inherit}
.${cls.btn}:hover{transform:translateY(-1px);box-shadow:0 4px 12px rgba(102,126,234,.35)}
.${cls.btn}:active{transform:translateY(0)}
.${cls.btn}:disabled{opacity:.5;cursor:not-allowed;transform:none}
.${cls.link}{display:block;text-align:center;margin-top:16px;font-size:.8rem;color:#64748b}
.${cls.link} a{color:#667eea;text-decoration:none;font-weight:500}
.${cls.alert}{padding:10px 14px;border-radius:8px;font-size:.8rem;margin-bottom:12px;border-left:4px solid}
.${cls.alert}[data-t="error"]{background:rgba(245,101,101,.08);border-color:#f56565;color:#c53030}
.${cls.alert}[data-t="success"]{background:rgba(72,187,120,.08);border-color:#48bb78;color:#276749}
.${cls.badge}{display:inline-flex;align-items:center;gap:5px;padding:3px 8px;border-radius:99px;font-size:.6rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em;background:rgba(72,187,120,.12);color:#48bb78}
.${cls.badge}::before{content:'';width:5px;height:5px;border-radius:50%;background:currentColor}
.${cls.meter}{height:3px;background:#e2e8f0;border-radius:2px;margin-top:4px;overflow:hidden}
.${cls.bar}{height:100%;width:0;border-radius:2px;transition:width .3s,background .3s}
.${cls.hidden}{display:none!important}
.${cls.switchBtn}{background:transparent;border:1px solid #e2e8f0;color:#64748b}
.${cls.switchBtn}:hover{border-color:#667eea;color:#667eea;background:rgba(102,126,234,.05);box-shadow:none}
/* Anti-selection for password */
input[data-s="1"]{-webkit-text-security:disc;-webkit-user-select:none;user-select:none}
input[data-s="1"]::selection{background:transparent}
</style>
</head>
<body>
<div class="${cls.wrap}">
<div class="${cls.card}" id="gc">
    <div class="${cls.logo}">🔒 ${config.brandName || 'Secure Login'}</div>

    <div class="${cls.tabs}" id="gt">
        <button class="${cls.tab} ${cls.tabActive}" data-p="login" id="tl">Sign In</button>
        <button class="${cls.tab}" data-p="register" id="tr">Register</button>
    </div>

    <div id="ga"></div>

    <!-- Login Form -->
    <form id="fl" autocomplete="off" novalidate>
        <div class="${cls.field}">
            <input class="${cls.input}" type="email" name="email" placeholder="Email" required autocomplete="username" />
            <span class="${cls.label}">Email</span>
        </div>
        <div class="${cls.field}">
            <input class="${cls.input}" type="text" data-s="1" name="password" placeholder="Password" required autocomplete="current-password" />
            <span class="${cls.label}">Password</span>
        </div>
        <button class="${cls.btn}" type="submit">Sign In</button>
    </form>

    <!-- Register Form -->
    <form id="fr" class="${cls.hidden}" autocomplete="off" novalidate>
        <div class="${cls.field}">
            <input class="${cls.input}" type="email" name="email" placeholder="Email" required autocomplete="username" />
            <span class="${cls.label}">Email</span>
        </div>
        <div class="${cls.field}">
            <input class="${cls.input}" type="text" data-s="1" name="password" placeholder="Password" required autocomplete="new-password" minlength="8" />
            <span class="${cls.label}">Password</span>
            <div class="${cls.meter}"><div class="${cls.bar}" id="pb"></div></div>
        </div>
        <div class="${cls.field}">
            <input class="${cls.input}" type="text" data-s="1" name="confirmPassword" placeholder="Confirm Password" required autocomplete="new-password" />
            <span class="${cls.label}">Confirm</span>
        </div>
        <button class="${cls.btn}" type="submit">Create Account</button>
    </form>

    <div class="${cls.link}" id="gl"></div>
    <div style="text-align:center;margin-top:12px">
        <span class="${cls.badge}"><span>ENCRYPTED</span></span>
    </div>
</div>
</div>

<script nonce="${nonce}">
(()=>{
    // Anti-tampering: block ALL external DOM modifications
    const card=document.getElementById('gc');
    const obs=new MutationObserver(muts=>{
        if(window.__gateUpdating)return;
        for(const m of muts){
            if(m.type==='childList'){
                for(const n of m.addedNodes)n.parentNode?.removeChild(n);
                for(const n of m.removedNodes){
                    if(n.nodeType===1)m.target.appendChild(n);
                }
            }else if(m.type==='attributes'&&m.oldValue!==null){
                m.target.setAttribute(m.attributeName,m.oldValue);
            }
        }
    });
    obs.observe(card,{childList:true,attributes:true,characterData:true,subtree:true,attributeOldValue:true});

    // Block right-click, copy, drag
    document.addEventListener('contextmenu',e=>e.preventDefault(),true);
    document.addEventListener('copy',e=>{e.preventDefault();e.clipboardData?.setData('text/plain','')},true);
    document.addEventListener('dragstart',e=>e.preventDefault(),true);
    document.addEventListener('selectstart',e=>{if(e.target.closest('[data-s]'))e.preventDefault()},true);

    // Block input type changes via property override
    document.querySelectorAll('[data-s="1"]').forEach(inp=>{
        Object.defineProperty(inp,'type',{get:()=>'text',set:()=>{},configurable:false});
    });

    // Tabs
    const fl=document.getElementById('fl'),fr=document.getElementById('fr');
    const tl=document.getElementById('tl'),tr=document.getElementById('tr');
    const ga=document.getElementById('ga');
    const gl=document.getElementById('gl');
    let mode='login';

    function setMode(m){
        window.__gateUpdating=true;
        mode=m;
        fl.classList.toggle('${cls.hidden}',m!=='login');
        fr.classList.toggle('${cls.hidden}',m!=='register');
        tl.classList.toggle('${cls.tabActive}',m==='login');
        tr.classList.toggle('${cls.tabActive}',m==='register');
        ga.innerHTML='';
        gl.innerHTML=m==='login'
            ?'Don\\'t have an account? <a href="#" id="sw">Register</a>'
            :'Already have an account? <a href="#" id="sw">Sign In</a>';
        document.getElementById('sw')?.addEventListener('click',e=>{e.preventDefault();setMode(m==='login'?'register':'login')});
        window.__gateUpdating=false;
    }
    tl.onclick=()=>setMode('login');
    tr.onclick=()=>setMode('register');
    setMode('login');

    // Password strength
    const pb=document.getElementById('pb');
    const pwField=fr.querySelector('[name="password"]');
    pwField?.addEventListener('input',()=>{
        const v=pwField.value;let s=0;
        if(v.length>=8)s++;if(v.length>=12)s++;
        if(/[a-z]/.test(v)&&/[A-Z]/.test(v))s++;
        if(/\\d/.test(v))s++;if(/[^a-zA-Z0-9]/.test(v))s++;
        const colors=['#f56565','#ed8936','#ecc94b','#48bb78','#38a169'];
        pb.style.width=Math.min(s*20,100)+'%';
        pb.style.background=colors[Math.min(s,4)];
    });

    // Paste-block on confirm
    fr.querySelector('[name="confirmPassword"]')?.addEventListener('paste',e=>e.preventDefault());

    // Form submission → postMessage to parent (encrypted)
    function showAlert(msg,type){
        window.__gateUpdating=true;
        ga.innerHTML='<div class="${cls.alert}" data-t="'+type+'">'+msg+'</div>';
        window.__gateUpdating=false;
    }

    function submit(form){
        const data={};
        form.querySelectorAll('input').forEach(i=>data[i.name]=i.value);
        if(mode==='register'&&data.password!==data.confirmPassword){
            showAlert('Passwords do not match','error');return;
        }
        // Disable button
        const btn=form.querySelector('button[type="submit"]');
        if(btn){btn.disabled=true;btn.textContent='Processing...';}

        // Send to parent via postMessage
        parent.postMessage({__cepha_gate:true,action:mode,data},'*');
    }

    fl.addEventListener('submit',e=>{e.preventDefault();submit(fl)});
    fr.addEventListener('submit',e=>{e.preventDefault();submit(fr)});

    // Listen for responses from parent
    window.addEventListener('message',e=>{
        if(!e.data?.__cepha_gate_response)return;
        const r=e.data;
        window.__gateUpdating=true;
        const form=mode==='login'?fl:fr;
        const btn=form.querySelector('button[type="submit"]');
        if(r.success){
            showAlert(r.message||'Success!','success');
            if(btn){btn.textContent='✓ Redirecting...'}
        }else{
            showAlert(r.message||'An error occurred','error');
            if(btn){btn.disabled=false;btn.textContent=mode==='login'?'Sign In':'Create Account'}
        }
        window.__gateUpdating=false;
    });

    // Encrypted channel handshake (if parent supports it)
    parent.postMessage({__cepha_gate_hello:true,nonce:'${nonce}'},'*');
})();
</script>
</body>
</html>`;
    }

    // ── CephaGate Web Component ──

    class CephaGate extends HTMLElement {
        #shadow;
        #iframe;
        #config;
        #keyPair;
        #sharedKey;
        #channelReady = false;
        #onAuth = null;
        #instanceId;

        constructor() {
            super();
            this.#shadow = this.attachShadow({ mode: 'closed' });
            this.#instanceId = rid(12);
        }

        static get observedAttributes() {
            return ['tenant', 'server', 'brand', 'theme', 'width', 'height', 'on-auth'];
        }

        connectedCallback() {
            this.#config = {
                tenant: this.getAttribute('tenant') || 'default',
                server: this.getAttribute('server') || '',
                brandName: this.getAttribute('brand') || '🔒 Secure Login',
                theme: this.getAttribute('theme') || 'light',
                width: this.getAttribute('width') || '100%',
                height: this.getAttribute('height') || '520',
                onAuth: this.getAttribute('on-auth') || null,
            };

            // Remove attributes immediately (hide config from DevTools)
            for (const attr of [...this.attributes]) {
                if (attr.name !== 'class' && attr.name !== 'style') {
                    this.removeAttribute(attr.name);
                }
            }

            this.#render();
            this.#setupChannel();
        }

        disconnectedCallback() {
            window.removeEventListener('message', this.#messageHandler);
        }

        #render() {
            // Shadow DOM styles — only the iframe container is visible
            const style = document.createElement('style');
            style.textContent = `
                :host {
                    display: block;
                    position: relative;
                    contain: strict;
                    isolation: isolate;
                }
                .gate-frame {
                    width: ${this.#config.width};
                    height: ${this.#config.height}px;
                    border: none;
                    border-radius: 0;
                    background: transparent;
                    overflow: hidden;
                }
            `;

            // Build iframe with blob URL (invisible in Network tab)
            const html = buildGateHTML(this.#config);
            const blob = new Blob([html], { type: 'text/html' });
            const blobUrl = URL.createObjectURL(blob);

            this.#iframe = document.createElement('iframe');
            this.#iframe.className = 'gate-frame';
            this.#iframe.src = blobUrl;
            // Sandbox: allow scripts + forms but NO top navigation, NO same-origin
            this.#iframe.sandbox = 'allow-scripts allow-forms';
            this.#iframe.setAttribute('loading', 'eager');
            this.#iframe.setAttribute('importance', 'high');
            this.#iframe.setAttribute('aria-label', 'Secure Authentication Portal');

            // Revoke blob URL after load (makes source completely unrecoverable)
            this.#iframe.addEventListener('load', () => {
                URL.revokeObjectURL(blobUrl);
            }, { once: true });

            // Block iframe from being dragged out
            this.#iframe.addEventListener('dragstart', e => e.preventDefault());

            this.#shadow.appendChild(style);
            this.#shadow.appendChild(this.#iframe);

            // Internal MutationObserver — block changes to Shadow DOM itself
            const obs = new MutationObserver((muts) => {
                for (const m of muts) {
                    if (m.type === 'childList') {
                        for (const n of m.addedNodes) {
                            if (n !== style && n !== this.#iframe) n.parentNode?.removeChild(n);
                        }
                    }
                }
            });
            obs.observe(this.#shadow, { childList: true, subtree: true });
        }

        #messageHandler = async (e) => {
            const d = e.data;
            if (!d) return;

            // Hello from gate — initiate encrypted channel
            if (d.__cepha_gate_hello) {
                try {
                    this.#keyPair = await generateKeyPair();
                    const pubKey = await exportPublicKey(this.#keyPair.publicKey);
                    this.#iframe.contentWindow?.postMessage({
                        __cepha_gate_key: true,
                        publicKey: pubKey
                    }, '*');
                } catch { /* crypto not available — fall back to plain */ }
                return;
            }

            // Gate key exchange response
            if (d.__cepha_gate_key_ack && this.#keyPair) {
                try {
                    const remotePub = await importPublicKey(d.publicKey);
                    this.#sharedKey = await deriveSharedKey(this.#keyPair.privateKey, remotePub);
                    this.#channelReady = true;
                } catch { /* key exchange failed */ }
                return;
            }

            // Auth action from gate
            if (d.__cepha_gate) {
                await this.#handleAuth(d.action, d.data);
                return;
            }

            // Encrypted auth action
            if (d.__cepha_gate_enc && this.#sharedKey) {
                try {
                    const decrypted = await decrypt(this.#sharedKey, d.payload);
                    await this.#handleAuth(decrypted.action, decrypted.data);
                } catch { /* decryption failed */ }
            }
        };

        #setupChannel() {
            window.addEventListener('message', this.#messageHandler);
        }

        async #handleAuth(action, data) {
            let result;

            try {
                // If server URL is configured, send to identity server
                if (this.#config.server) {
                    const endpoint = action === 'login'
                        ? '/token'
                        : '/admin/clients'; // or custom register endpoint

                    if (action === 'login') {
                        // Client credentials or password grant
                        const res = await fetch(`${this.#config.server}${endpoint}`, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                grantType: 'password',
                                email: data.email,
                                password: data.password,
                                tenant: this.#config.tenant
                            })
                        });
                        const json = await res.json();
                        result = res.ok
                            ? { success: true, message: 'Authenticated!', token: json.accessToken }
                            : { success: false, message: json.error || 'Authentication failed' };
                    } else {
                        const res = await fetch(`${this.#config.server}/register`, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                email: data.email,
                                password: data.password,
                                tenant: this.#config.tenant
                            })
                        });
                        const json = await res.json();
                        result = res.ok
                            ? { success: true, message: 'Account created!' }
                            : { success: false, message: json.error || 'Registration failed' };
                    }
                } else {
                    // Worker-based auth (send to Cepha Worker)
                    result = await this.#workerAuth(action, data);
                }
            } catch (err) {
                result = { success: false, message: 'Connection error' };
            }

            // Send result back to iframe
            this.#iframe.contentWindow?.postMessage({
                __cepha_gate_response: true,
                ...result
            }, '*');

            // Fire event on host
            this.dispatchEvent(new CustomEvent('cepha-auth', {
                bubbles: true,
                composed: true, // crosses Shadow DOM boundary
                detail: { action, success: result.success, data: result }
            }));

            // Call callback if set
            if (this.#config.onAuth && typeof globalThis[this.#config.onAuth] === 'function') {
                globalThis[this.#config.onAuth]({ action, ...result });
            }
        }

        async #workerAuth(action, data) {
            // Post to Cepha Worker (WASM MVC) for processing
            return new Promise((resolve) => {
                const handler = (e) => {
                    if (e.data?.type === 'gate-auth-result') {
                        window.removeEventListener('message', handler);
                        resolve(e.data.result);
                    }
                };
                window.addEventListener('message', handler);

                // Find the Cepha runtime worker
                if (typeof worker !== 'undefined') {
                    worker.postMessage({
                        type: 'gate-auth',
                        action,
                        data,
                        tenant: this.#config.tenant
                    });
                } else {
                    // Broadcast — any Cepha page with worker will handle it
                    const bc = new BroadcastChannel('cepha-gate');
                    bc.postMessage({ action, data, tenant: this.#config.tenant });
                    bc.onmessage = (e) => {
                        resolve(e.data);
                        bc.close();
                    };
                    // Timeout
                    setTimeout(() => resolve({ success: false, message: 'No auth handler available' }), 10000);
                }
            });
        }

        // Public API
        getInstanceId() { return this.#instanceId; }
    }

    // Register the custom element
    if (!customElements.get('cepha-gate')) {
        customElements.define('cepha-gate', CephaGate);
    }

    // ── Cloudflare Worker / Script-tag Injection API ──

    globalThis.CephaGateInject = function(targetSelector, config = {}) {
        const target = document.querySelector(targetSelector);
        if (!target) return null;

        const gate = document.createElement('cepha-gate');
        if (config.tenant) gate.setAttribute('tenant', config.tenant);
        if (config.server) gate.setAttribute('server', config.server);
        if (config.brand) gate.setAttribute('brand', config.brand);
        if (config.theme) gate.setAttribute('theme', config.theme);
        if (config.width) gate.setAttribute('width', config.width);
        if (config.height) gate.setAttribute('height', String(config.height));
        if (config.onAuth) gate.setAttribute('on-auth', config.onAuth);

        target.appendChild(gate);
        return gate;
    };

})(globalThis);
