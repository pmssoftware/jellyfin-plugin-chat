(function jellyfinChatTabBridge() {
    'use strict';
    if (window.jellyfinChatTabBridge) return;

    const bridge = window.jellyfinChatTabBridge = {
        running: false,
        scheduled: false,
        complete: false,
        lastAttemptAt: 0,
        isHome() {
            const hash = window.location.hash;
            return hash === '' || hash === '#/home' || hash === '#/home.html' || hash.includes('#/home?') || hash.includes('#/home.html?');
        },
        request(path) {
            return ApiClient.fetch({url:ApiClient.getUrl(path),type:'GET',dataType:'json',headers:{accept:'application/json'}});
        },
        normalizePaneOrder(favorites) {
            const panes=Array.from(document.querySelectorAll('[id^="customTab_"]')).sort((left,right)=>Number(left.id.slice(10))-Number(right.id.slice(10)));
            let anchor=favorites;
            panes.forEach(pane=>{if(anchor.nextElementSibling!==pane)anchor.insertAdjacentElement('afterend',pane);anchor=pane;});
        },
        ensureAdminLink() {
            let link = document.querySelector('a[href*="configurationpage?name=jellyfin-chat"]');
            if (!link) {
                const source = document.querySelector('.mainDrawer a[href*="configurationpage?name=content-requests"]')
                    || document.querySelector('.mainDrawer a[href*="configurationpage?name="]');
                if (!source) return;
                link = source.cloneNode(true);
                link.id = 'jellyfinChatAdminLink';
                link.href = '#/configurationpage?name=jellyfin-chat';
                link.removeAttribute('data-itemid');
                link.addEventListener('click', event => {
                    event.preventDefault();
                    window.location.hash = '#/configurationpage?name=jellyfin-chat';
                });
                source.insertAdjacentElement('afterend', link);
            }

            const label = link.querySelector('.navMenuOptionText, .emby-button-foreground');
            if (label) label.textContent = 'Jellyfin Chat';
            const current = link.querySelector('svg, .material-icons, .material-symbols-rounded');
            if (!current || current.dataset.jellyfinChatIcon === 'true') return;
            const icon = document.createElement('span');
            icon.className = 'material-icons';
            icon.dataset.jellyfinChatIcon = 'true';
            icon.setAttribute('aria-hidden', 'true');
            icon.style.fontSize = '1.5rem';
            icon.textContent = 'forum';
            current.replaceWith(icon);
        },
        ensureSidebarLink(tabName, enabled, index) {
            let link = document.getElementById('jellyfinChatSidebarLink');
            if (!link) {
                const home = document.querySelector('.mainDrawer a[href="#/home"], .mainDrawer a[href="#/home.html"], a.navMenuOption[href="#/home"]');
                if (!home) return;
                link = home.cloneNode(true); link.id = 'jellyfinChatSidebarLink'; link.href = '#/home'; link.removeAttribute('data-itemid');
                link.addEventListener('click', event => {
                    event.preventDefault(); window.location.hash = '#/home';
                    window.setTimeout(() => { bridge.schedule(); const button=document.getElementById('customTabButton_'+index); if(button)button.click(); }, 500);
                });
                const requests = document.getElementById('contentRequestsSidebarLink');
                (requests || home).insertAdjacentElement('afterend', link);
            }
            link.style.display = enabled ? '' : 'none';
            const label = link.querySelector('.navMenuOptionText, .emby-button-foreground'); if (label) label.textContent = tabName;
            const existing = link.querySelector('svg, .material-icons, .material-symbols-rounded'); const container = existing && existing.parentElement;
            if (container && container.dataset.jellyfinChatIcon !== 'true') {
                const icon=document.createElement('span'); icon.className='material-icons'; icon.setAttribute('aria-hidden','true'); icon.style.cssText='display:inline-flex;align-items:center;justify-content:center;width:1.5rem;flex:0 0 1.5rem;margin-right:1.2rem;font-size:1.5rem'; icon.textContent='forum';
                if (container===link || (label && container.contains(label))) { link.querySelectorAll('svg,.material-icons,.material-symbols-rounded').forEach(item=>item.remove()); label?link.insertBefore(icon,label):link.prepend(icon); link.dataset.jellyfinChatIcon='true'; }
                else { container.replaceChildren(icon); container.dataset.jellyfinChatIcon='true'; }
            }
        },
        async repair() {
            this.ensureAdminLink();
            if (this.running || !this.isHome() || typeof ApiClient === 'undefined') return;
            if (this.complete && document.getElementById('jellyfinChatSidebarLink')) return;
            if (Date.now() - this.lastAttemptAt < 1000) return;
            this.lastAttemptAt = Date.now();
            this.running = true;
            try {
                const configs = await this.request('CustomTabs/Config');
                const index = configs.findIndex(config => String(config.ContentHtml || '').includes('JellyfinChat/App'));
                if (index < 0) return;
                const button=document.getElementById('customTabButton_'+index), favorites=document.getElementById('favoritesTab');
                if (!button || !favorites) return;
                const settings=await this.request('JellyfinChat/Availability');
                const enabled=settings.Available??settings.available??false, tabName=settings.TabName??settings.tabName??'Chat';
                button.style.display=enabled?'':'none'; const label=button.querySelector('.emby-button-foreground'); if(label)label.textContent=tabName;
                this.ensureSidebarLink(tabName,enabled,index);
                let pane=document.getElementById('customTab_'+index);
                if(!pane){pane=document.createElement('div');pane.id='customTab_'+index;pane.className='tabContent pageTabContent';pane.dataset.index=String(index+2);favorites.insertAdjacentElement('afterend',pane);}
                let iframe=pane.querySelector('iframe');
                if(!iframe||!String(iframe.getAttribute('src')||'').includes('JellyfinChat/App')){iframe=document.createElement('iframe');iframe.title=tabName;iframe.src=ApiClient.getUrl('JellyfinChat/App');iframe.style.cssText='display:block;width:100%;height:calc(100vh - 7.5rem);min-height:32rem;border:0;background:transparent';pane.replaceChildren(iframe);}
                this.normalizePaneOrder(favorites);
                pane.style.display=enabled?'':'none';
                this.complete=true;
            } catch(error) { console.debug('Jellyfin Chat: waiting for CustomTabs.',error); }
            finally { this.running=false; }
        },
        schedule(){if(this.scheduled)return;this.scheduled=true;window.setTimeout(()=>{this.scheduled=false;this.repair();},100);}
    };
    new MutationObserver(()=>bridge.schedule()).observe(document.documentElement,{childList:true,subtree:true});
    window.addEventListener('hashchange',()=>{bridge.complete=false;bridge.schedule();}); window.addEventListener('pageshow',()=>{bridge.complete=false;bridge.schedule();}); bridge.schedule();
}());
