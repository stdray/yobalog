# YobaLog — развёртывание

Пошаговая инструкция для первой выкатки yobalog на shared хост + последующих релизов. Написано в первую очередь для будущего-меня: когда дойдут руки реально задеплоить, не хочется вспоминать "а что там с Caddy нужно было".

**Контекст.** Yobalog деплоится первым из стека (yobaconf / yobapub / KpVotes / ...) под host-level Caddy, который терминирует TLS на `:443` и прокси'т на loopback-порты. Host-port convention:
- `127.0.0.1:8080` → yobapub.3po.su (legacy, на своём nginx+certbot)
- `127.0.0.1:8081` → yobaconf.3po.su
- `127.0.0.1:8082` → **yobalog.3po.su** (этот сервис)

Решение и причины — `doc/decision-log.md` запись 2026-04-21 "Caddy on host as HTTPS terminator; yobalog deploys first". Спецификация — `doc/spec.md` §11.

---

## Pre-requisites (один раз)

- **Сервер.** Ubuntu 22.04/24.04 (или аналог), root/sudo доступ, открытые входящие `80/tcp` и `443/tcp`. IPv4 обязательно; IPv6 опционально.
- **DNS-контроль** над зоной `3po.su` (для A-записей).
- **Public GitHub repo + public GHCR package.** `publish`-job пушит образ в `ghcr.io` используя автоматический `${{ secrets.GITHUB_TOKEN }}` (workflow top-level `permissions: packages: write`) — отдельного PAT не нужно. Deploy-job на VM `docker pull`-ит анонимно, тоже без PAT. Если когда-то репо станет private — вернуть `GHCR_DEPLOY_USERNAME` / `GHCR_DEPLOY_TOKEN` secrets и `docker login ghcr.io` в deploy-script. После первого push'а в GHCR надо один раз **вручную** сменить visibility package'а на public: GitHub profile → Packages → yobalog → Package settings → Change visibility.
- **GitHub secrets** в этом репо (Settings → Secrets → Actions):
    - `DEPLOY_HOST` — `yoba-apps.3po.su` или IP (CI подключается по SSH).
    - `DEPLOY_USERNAME` — пользователь на сервере с правом `docker` (через группу `docker`, не sudo NOPASSWD).
    - `DEPLOY_PASSWORD` — SSH-пароль этого пользователя (он же sudo-пароль). SSH на сервере оставлен с `PasswordAuthentication yes` для CI-дружественного flow; `PermitRootLogin no` обязательно.
    - `YOBALOG_ADMIN_USERNAME` / `YOBALOG_ADMIN_PASSWORD` — первичный admin для cookie-auth (через DB-миграцию в `/admin/users` потом добавятся остальные).

---

## First-time host bootstrap (один раз, **только при первом сервисе под Caddy на хосте**)

Yobalog — первый сервис под Caddy. После этих шагов центральный `/etc/caddy/Caddyfile` уже существует, и все последующие проекты (yobaconf следующим) просто добавляют в него свой fragment + `systemctl reload caddy`.

### 1. DNS

Добавить A-запись `yobalog.3po.su` → IP сервера. TTL 300s на время провиженинга (можно поднять потом).

```
yobalog.3po.su.    300    IN    A    <server-ip>
```

Проверить распространение: `dig yobalog.3po.su +short` с любой рабочей машины.

### 2. Установить Caddy на сервер

```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install -y caddy
```

Пакет ставит systemd-service `caddy.service`, пользователя `caddy`, дефолтный `/etc/caddy/Caddyfile` (указывающий на `/var/www/html`).

### 3. Scaffold центрального Caddyfile

Скопировать содержимое `infra/Caddyfile.fragment` из этого репо → `/etc/caddy/Caddyfile` на сервере, **заменив дефолтный контент полностью** (дефолтный — just-installed example, не нужен).

```bash
sudo tee /etc/caddy/Caddyfile > /dev/null <<'CADDYFILE'
# <paste contents of infra/Caddyfile.fragment here>
CADDYFILE

sudo caddy fmt --overwrite /etc/caddy/Caddyfile
sudo caddy validate --config /etc/caddy/Caddyfile
```

`caddy validate` должен вернуть `Valid configuration`. Если `validate` фейлит — конфиг отвергаем и чиним, не стартуем.

### 4. Firewall на 80/443

```bash
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 22/tcp    # убедиться что SSH не отрубится
sudo ufw enable
sudo ufw status verbose
```

Если `ufw` недоступен — iptables-эквивалент (`iptables -A INPUT -p tcp --dport 443 -j ACCEPT`, etc.). Главное — Let's Encrypt ACME HTTP-01 challenge ходит на `:80`; без открытого 80 cert не выпустится.

### 5. Стартовать Caddy

```bash
sudo systemctl enable caddy
sudo systemctl start caddy
sudo systemctl status caddy
```

Логи: `sudo journalctl -u caddy -f`. При первом HTTPS-запросе Caddy сходит в Let's Encrypt за сертификатом, положит в `/var/lib/caddy/.local/share/caddy/certificates/`. Renewal — автоматически, cron-less.

### 6. Data-каталог для контейнера

Chiseled-образ yobalog запускается под user'ом `app` (UID 1654) — наш SQLite должен уметь писать в mount'нутый volume. Если `/opt/yobalog/data` не существует, Docker создаст его при первом `docker run -v ...`, но **root-owned** → контейнер упадёт с permission denied. Создаём явно:

```bash
sudo mkdir -p /opt/yobalog/data
sudo chown 1654:1654 /opt/yobalog/data
```

Одноразовый шаг — переживает redeploy, rebuild, restart.

Пользователь `stdray` уже в группе `docker` (из Docker-секции выше), поэтому CI deploy-job не требует sudo для команд `docker pull/stop/rm/run/prune` — сразу работает через docker.sock.

### 7. GitHub secrets

Через Settings → Secrets → Actions заполнить все из Pre-requisites выше.

### 8. Первый deploy

```bash
# локально, в корне репо:
git tag deploy
git push origin deploy --force
```

CI задеплоит: build image → `docker push ghcr.io/<owner>/yobalog:<sha>` → SSH на сервер → `docker pull` → `docker run -d -p 127.0.0.1:8082:8080 ...`. После этого первый `curl https://yobalog.3po.su/health` триггерит Caddy'шный ACME flow → через 5-30 секунд появляется cert.

`--force` нужен потому что тег `deploy` переиспользуется — каждый деплой move'ает его на новый commit.

### 9. Sanity check

```bash
# с любой машины:
curl -i https://yobalog.3po.su/health
# → HTTP/2 200, body {"status":"healthy"}

curl -i https://yobalog.3po.su/version
# → build provenance (semVer, shortSha, commitDate)

# login page открывается:
curl -sI https://yobalog.3po.su/Login
# → HTTP/2 200

# http → https redirect работает:
curl -sI http://yobalog.3po.su/
# → HTTP/1.1 308 Permanent Redirect, Location: https://...
```

Если всё зелёное — хост-setup закончен. Все последующие `git tag deploy && git push -f` используют уже готовую инфраструктуру.

---

## Последующие релизы (regular flow)

```bash
git tag deploy
git push origin deploy --force
```

CI прогоняет `test` + `e2e` → если зелёные → `publish` (docker push ghcr) → `deploy` (SSH + `docker run`). Логи прогона — в GitHub Actions. На сервере старый контейнер `docker stop` + `docker rm`, новый стартует с тем же `-p 127.0.0.1:8082:8080`.

Данные (`.logs.db`, `.meta.db`, `$system.meta.db`) mount'яется как volume `/opt/yobalog/data:/app/data` — переживает redeploy.

---

## Добавление следующего сервиса под Caddy (yobaconf next)

Когда будешь деплоить yobaconf (или любой новый HTTPS-сервис):

1. В его репо скопировать `infra/Caddyfile.fragment` с правильным портом (`127.0.0.1:8081` для yobaconf, etc).
2. На сервере: **append** fragment в `/etc/caddy/Caddyfile` (не перезаписывать — yobalog'овский блок должен остаться).
3. `sudo caddy validate --config /etc/caddy/Caddyfile`.
4. `sudo systemctl reload caddy` (не restart — reload hot-свапит конфиг без обрыва соединений).
5. DNS A-запись для нового subdomain → тот же IP.
6. `git tag deploy && git push -f` в его репо.

`apt install caddy` + `ufw` + `systemctl enable` — **не нужны**, уже сделано yobalog'ом. Deploy-doc следующего сервиса начинается сразу с "добавить fragment".

---

## Troubleshooting

- **`curl https://yobalog.3po.su` → connection refused.** Caddy не стартанул или firewall режет 443. `sudo systemctl status caddy`; `sudo ufw status`.
- **`curl https://yobalog.3po.su/health` → 502 Bad Gateway.** Caddy жив, но контейнер не отвечает на `127.0.0.1:8082`. `docker ps` на сервере — контейнер жив? `docker logs yobalog` — стартанул ли процесс?
- **`curl https://yobalog.3po.su/health` → redirect loop.** ForwardedHeaders не настроен или не trust'ает loopback — см. `YobaLogApp.ConfigureServices` (`ForwardedHeadersOptions`).
- **Cert не выпускается.** `sudo journalctl -u caddy | grep -i acme`. Частые причины: (a) порт 80 закрыт firewall'ом → ACME HTTP-01 challenge не проходит; (b) DNS ещё не пропагировался → Let's Encrypt не видит домен на сервере; (c) rate limit Let's Encrypt если делал много попыток подряд (5 certs/hour per domain).
- **Live-tail (SSE) показывает события батчами, а не realtime.** `flush_interval -1` не применился — проверить что он внутри `reverse_proxy` block'а в `/etc/caddy/Caddyfile`. См. `infra/Caddyfile.fragment` reference.
- **Share-link URL приходит с `http://` вместо `https://`.** ForwardedHeaders пропустил `X-Forwarded-Proto`. `RemoteIpAddress` request'а должен быть в `KnownProxies` (`127.0.0.1` / `::1`). Если Caddy на том же хосте, что и контейнер — должно работать из коробки.
