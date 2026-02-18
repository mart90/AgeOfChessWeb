#!/usr/bin/env python3
"""
Goldrush Gambit — deployment script
Must be run as root (sudo python3 deploy.py ...).

Usage:
  Full deploy (server + frontend):
    sudo python3 deploy.py

  Frontend only (no service restart):
    sudo python3 deploy.py --frontend-only

  Override repo path:
    sudo python3 deploy.py --repo /path/to/AgeOfChessWeb

First-deploy checklist:
  1. Ensure .NET 9 runtime/SDK is installed  (dotnet --version)
  2. Ensure Node.js + npm are installed       (node --version, npm --version)
  3. Place your production appsettings on the server — this is NEVER overwritten by this script:
       /var/www/goldrushgambit/appsettings.Production.json
  4. Run a full deploy to set up the service and nginx config.
  5. Run: sudo certbot --nginx -d goldrushgambit.com -d www.goldrushgambit.com
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

# ── Configuration ──────────────────────────────────────────────────────────────
REPO_DEFAULT = Path('/var/www/AgeOfChessWeb')
DEPLOY_PATH  = Path('/var/www/goldrushgambit')
PUBLISH_TMP  = Path('/tmp/goldrushgambit-publish')
SERVICE_NAME = 'goldrushgambit'
SERVICE_USER = 'www-data'
DOMAIN       = 'goldrushgambit.com'
PORT         = 5004
# ───────────────────────────────────────────────────────────────────────────────

CYAN  = '\033[36m'
GREEN = '\033[1;32m'
YELL  = '\033[1;33m'
RED   = '\033[31m'
RESET = '\033[0m'


def run(cmd, cwd=None):
    print(f'{CYAN}$ {cmd}{RESET}')
    result = subprocess.run(cmd, shell=True, cwd=cwd)
    if result.returncode != 0:
        print(f'{RED}Error: command exited with code {result.returncode}{RESET}')
        sys.exit(result.returncode)


def step(title):
    print(f'\n{YELL}── {title} ──{RESET}')


def git_pull(repo):
    step('Fetching latest code')
    run('git pull', cwd=repo)


def build_client(repo):
    step('Building client')
    run('npm ci', cwd=repo / 'client')
    run('npm run build', cwd=repo / 'client')


def build_server(repo):
    step('Publishing server')
    if PUBLISH_TMP.exists():
        run(f'rm -rf {PUBLISH_TMP}')
    run(
        f'dotnet publish AgeOfChess.Server/AgeOfChess.Server.csproj '
        f'-c Release -o {PUBLISH_TMP}',
        cwd=repo,
    )


def deploy_full():
    step(f'Deploying to {DEPLOY_PATH}')
    DEPLOY_PATH.mkdir(parents=True, exist_ok=True)
    # appsettings.Production.json lives on the server only and is never overwritten.
    run(
        f'rsync -a --delete '
        f'--exclude "appsettings.Production.json" '
        f'{PUBLISH_TMP}/ {DEPLOY_PATH}/'
    )
    run(f'chown -R {SERVICE_USER}:{SERVICE_USER} {DEPLOY_PATH}')


def deploy_frontend_only(repo):
    step('Deploying frontend only')
    wwwroot_src = repo / 'AgeOfChess.Server' / 'wwwroot'
    wwwroot_dst = DEPLOY_PATH / 'wwwroot'
    run(f'rsync -a --delete {wwwroot_src}/ {wwwroot_dst}/')
    run(f'chown -R {SERVICE_USER}:{SERVICE_USER} {wwwroot_dst}')
    print('Static files updated — no service restart needed.')


def ensure_service():
    service_file = Path(f'/etc/systemd/system/{SERVICE_NAME}.service')
    if service_file.exists():
        return
    step('Creating systemd service')
    content = f"""\
[Unit]
Description=Goldrush Gambit
After=network.target mysql.service

[Service]
Type=simple
User={SERVICE_USER}
WorkingDirectory={DEPLOY_PATH}
ExecStart=/usr/bin/dotnet {DEPLOY_PATH}/AgeOfChess.Server.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:{PORT}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
"""
    service_file.write_text(content)
    run('systemctl daemon-reload')
    run(f'systemctl enable {SERVICE_NAME}')
    print('Service created and enabled.')


def restart_service():
    step('Restarting service')
    run(f'systemctl restart {SERVICE_NAME}')
    run(f'systemctl status {SERVICE_NAME} --no-pager -l')


def ensure_nginx():
    conf_path    = Path(f'/etc/nginx/sites-available/{SERVICE_NAME}')
    enabled_path = Path(f'/etc/nginx/sites-enabled/{SERVICE_NAME}')
    if conf_path.exists():
        return
    step('Creating nginx config')
    # map directive must live at http level; sites-available files are included there.
    content = f"""\
map $http_upgrade $connection_upgrade {{
    default upgrade;
    ''      close;
}}

server {{
    listen 80;
    server_name {DOMAIN} www.{DOMAIN};

    location / {{
        proxy_pass         http://localhost:{PORT};
        proxy_http_version 1.1;
        proxy_set_header   Upgrade    $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host       $host;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
        proxy_read_timeout 100s;
    }}
}}
"""
    conf_path.write_text(content)
    if not enabled_path.exists():
        enabled_path.symlink_to(conf_path)
    run('nginx -t')
    run('systemctl reload nginx')
    print('nginx config created and reloaded.')


def main():
    parser = argparse.ArgumentParser(description='Deploy Goldrush Gambit')
    parser.add_argument(
        '--repo', type=Path, default=REPO_DEFAULT,
        help=f'Path to the cloned git repository on this server (default: {REPO_DEFAULT})',
    )
    parser.add_argument(
        '--frontend-only', action='store_true',
        help='Rebuild and copy only the frontend — skips server build and service restart',
    )
    args = parser.parse_args()

    repo = args.repo.resolve()
    if not (repo / '.git').is_dir():
        print(f'{RED}Error: {repo} does not look like a git repository{RESET}')
        sys.exit(1)

    label = '  [frontend only]' if args.frontend_only else ''
    print(f'{GREEN}=== Goldrush Gambit Deployment{label} ==={RESET}')
    print(f'Repo:   {repo}')
    print(f'Deploy: {DEPLOY_PATH}')

    git_pull(repo)
    build_client(repo)

    if args.frontend_only:
        deploy_frontend_only(repo)
    else:
        build_server(repo)
        ensure_service()
        deploy_full()
        restart_service()
        ensure_nginx()

    print(f'\n{GREEN}=== Done ==={RESET}')


if __name__ == '__main__':
    if os.geteuid() != 0:
        print(f'{RED}Error: this script must be run as root (sudo python3 deploy.py ...){RESET}')
        sys.exit(1)
    main()
