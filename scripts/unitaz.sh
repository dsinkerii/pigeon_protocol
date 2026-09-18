#!/usr/bin/env bash
# ==============================================================================
# Unitaz Relay Management Script (inspired by 3x-ui)
# Libregram Backend Relay Controller
# ==============================================================================

set -e

# Color definitions
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
PURPLE='\033[0;35m'
CYAN='\033[0;36m'
WHITE='\033[1;37m'
NC='\033[0m' # No Color
BOLD='\033[1m'

SERVICE_NAME="unitaz-relay"
INSTALL_DIR="/opt/unitaz-relay"
SYSTEMD_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
SCRIPT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_PATH")"
APPSETTINGS_PATH="${INSTALL_DIR}/appsettings.json"

# Check root permissions
check_root() {
    if [[ $EUID -ne 0 ]]; then
        echo -e "${RED}Error: This script must be run as root (or using sudo).${NC}"
        exit 1
    fi
}

# Status checker
is_service_running() {
    if systemctl is-active --quiet "${SERVICE_NAME}" 2>/dev/null; then
        return 0
    else
        return 1
    fi
}

is_service_enabled() {
    if systemctl is-enabled --quiet "${SERVICE_NAME}" 2>/dev/null; then
        return 0
    else
        return 1
    fi
}

get_service_status_text() {
    if is_service_running; then
        echo -e "${GREEN}Running (Active)${NC}"
    else
        echo -e "${RED}Stopped (Inactive)${NC}"
    fi
}

get_autostart_status_text() {
    if is_service_enabled; then
        echo -e "${GREEN}Enabled${NC}"
    else
        echo -e "${YELLOW}Disabled${NC}"
    fi
}

get_server_port() {
    local config_file="${INSTALL_DIR}/appsettings.json"
    if [[ ! -f "$config_file" && -f "${REPO_DIR}/src/Netstr/appsettings.json" ]]; then
        config_file="${REPO_DIR}/src/Netstr/appsettings.json"
    fi
    if [[ -f "$config_file" ]]; then
        local port=$(grep -o '"Port":\s*[0-9]*' "$config_file" | head -n 1 | awk -F: '{print $2}' | tr -d ' ')
        if [[ -n "$port" ]]; then
            echo "$port"
            return
        fi
    fi
    echo "8083"
}

get_server_host() {
    local config_file="${INSTALL_DIR}/appsettings.json"
    if [[ ! -f "$config_file" && -f "${REPO_DIR}/src/Netstr/appsettings.json" ]]; then
        config_file="${REPO_DIR}/src/Netstr/appsettings.json"
    fi
    if [[ -f "$config_file" ]]; then
        local host=$(grep -o '"Host":\s*"[^"]*"' "$config_file" | head -n 1 | awk -F'"' '{print $4}')
        if [[ -n "$host" ]]; then
            echo "$host"
            return
        fi
    fi
    echo "0.0.0.0"
}

get_local_ip() {
    ip route get 1.1.1.1 2>/dev/null | awk '{print $7}' | head -n 1 || hostname -I 2>/dev/null | awk '{print $1}' || echo "127.0.0.1"
}

get_panel_port() {
    get_server_port
}

print_banner() {
    clear
    local s_host=$(get_server_host)
    local s_port=$(get_server_port)
    local lan_ip=$(get_local_ip)

    echo -e "${CYAN}================================================================${NC}"
    echo -e "${BOLD}${WHITE}               UNITAZ RELAY MANAGEMENT SCRIPT                  ${NC}"
    echo -e "${PURPLE}           High-Performance Libregram Node Controller           ${NC}"
    echo -e "${CYAN}================================================================${NC}"
    echo -e " Service Status: $(get_service_status_text)"
    echo -e " Autostart:      $(get_autostart_status_text)"
    echo -e " Listening Host: ${YELLOW}${s_host}${NC} (Local LAN IP: ${CYAN}${lan_ip}${NC})"
    echo -e " Listening Port: ${YELLOW}${s_port}${NC}"
    if [[ "$s_host" == "0.0.0.0" || "$s_host" == "*" ]]; then
        echo -e " Panel URL:      ${GREEN}http://${lan_ip}:${s_port}/admin${NC} (or http://localhost:${s_port}/admin)"
    elif [[ "$s_host" == "127.0.0.1" || "$s_host" == "localhost" ]]; then
        echo -e " Panel URL:      ${YELLOW}http://localhost:${s_port}/admin${NC} ${RED}(Localhost Only - Router blocked!)${NC}"
    else
        echo -e " Panel URL:      ${GREEN}http://${s_host}:${s_port}/admin${NC}"
    fi
    echo -e "${CYAN}----------------------------------------------------------------${NC}"
}

# 1. Service operations
ensure_docker_postgres() {
    if command -v docker >/dev/null 2>&1; then
        local container=$(docker ps -a --filter "name=netstr-postgres" --format "{{.Names}}" | head -n 1)
        if [[ -z "$container" ]]; then
            container=$(docker ps -a --filter "ancestor=postgres:16-alpine" --format "{{.Names}}" | head -n 1)
        fi
        if [[ -z "$container" ]]; then
            container=$(docker ps -a --filter "name=postgres" --format "{{.Names}}" | head -n 1)
        fi

        if [[ -n "$container" ]]; then
            # Ensure container is set to restart unless stopped so docker boots it automatically
            docker update --restart unless-stopped "$container" >/dev/null 2>&1 || true

            if ! docker ps --format "{{.Names}}" | grep -q "^${container}$"; then
                echo -e "${YELLOW}Starting PostgreSQL container '${container}'...${NC}"
                docker start "$container" >/dev/null 2>&1
                sleep 2
            fi
        fi
    fi
}

check_and_free_port() {
    local port="$1"
    [[ -z "$port" ]] && port=$(get_server_port)
    local service_pid
    service_pid=$(systemctl show --property MainPID --value "${SERVICE_NAME}" 2>/dev/null || true)

    local listening_pids
    listening_pids=$(ss -tulpn 2>/dev/null | grep ":${port} " | awk '{print $NF}' | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u)

    for pid in $listening_pids; do
        if [[ -n "$service_pid" && "$pid" == "$service_pid" ]]; then
            continue
        fi
        local pname
        pname=$(ps -p "$pid" -o comm= 2>/dev/null || echo "unknown")
        echo -e "${YELLOW}Warning: Port ${port} is occupied by non-service process PID ${pid} (${pname}). Freeing port...${NC}"
        kill -15 "$pid" 2>/dev/null || true
        sleep 1
        if kill -0 "$pid" 2>/dev/null; then
            kill -9 "$pid" 2>/dev/null || true
        fi
    done
}

service_start() {
    ensure_docker_postgres
    check_and_free_port
    echo -e "\n${BLUE}Starting ${SERVICE_NAME}...${NC}"
    systemctl start "${SERVICE_NAME}"
    sleep 1
    if is_service_running; then
        echo -e "${GREEN}Successfully started ${SERVICE_NAME}!${NC}"
    else
        echo -e "${RED}Failed to start ${SERVICE_NAME}. Check journalctl for logs.${NC}"
    fi
}

service_stop() {
    echo -e "\n${BLUE}Stopping ${SERVICE_NAME}...${NC}"
    systemctl stop "${SERVICE_NAME}"
    sleep 1
    echo -e "${YELLOW}${SERVICE_NAME} stopped.${NC}"
}

service_restart() {
    ensure_docker_postgres
    echo -e "\n${BLUE}Restarting ${SERVICE_NAME}...${NC}"
    systemctl stop "${SERVICE_NAME}" 2>/dev/null || true
    sleep 1
    check_and_free_port
    systemctl start "${SERVICE_NAME}"
    sleep 1
    if is_service_running; then
        echo -e "${GREEN}Successfully restarted ${SERVICE_NAME}!${NC}"
    else
        echo -e "${RED}Failed to restart ${SERVICE_NAME}. Check journalctl for logs.${NC}"
    fi
}

service_status_details() {
    echo -e "\n${CYAN}--- ${SERVICE_NAME} Service Status ---${NC}"
    systemctl status "${SERVICE_NAME}" --no-pager || true
}

view_live_logs() {
    echo -e "\n${CYAN}Streaming live logs (Press Ctrl+C to exit)...${NC}"
    journalctl -u "${SERVICE_NAME}" -f -n 50
}

# 2. Installation & Setup
install_unitaz_relay() {
    echo -e "\n${BOLD}${CYAN}--- Install / Deploy Unitaz Relay ---${NC}"
    echo -e "Target directory: ${INSTALL_DIR}"
    ensure_docker_postgres

    # Build binaries if running from repository
    if [[ -d "${REPO_DIR}/src/Netstr" ]]; then
        echo -e "${BLUE}Building release binaries from repository...${NC}"
        dotnet publish "${REPO_DIR}/src/Netstr/Netstr.csproj" -c Release -o "${INSTALL_DIR}"
        rm -f "${INSTALL_DIR}/wwwroot/admin/index.html.br" "${INSTALL_DIR}/wwwroot/admin/index.html.gz"
    else
        echo -e "${YELLOW}Warning: Repository source not found at ${REPO_DIR}/src/Netstr.${NC}"
        echo -e "Ensuring ${INSTALL_DIR} exists..."
        mkdir -p "${INSTALL_DIR}"
    fi

    # Copy service unit
    if [[ -f "${SCRIPT_PATH}/unitaz-relay.service" ]]; then
        echo -e "${BLUE}Installing systemd unit to ${SYSTEMD_FILE}...${NC}"
        cp "${SCRIPT_PATH}/unitaz-relay.service" "${SYSTEMD_FILE}"
    else
        echo -e "${RED}Error: unitaz-relay.service template not found in ${SCRIPT_PATH}.${NC}"
        return 1
    fi

    # Setup symlink to /usr/local/bin/unitaz
    if [[ ! -f "/usr/local/bin/unitaz" ]]; then
        ln -sf "${SCRIPT_PATH}/unitaz.sh" "/usr/local/bin/unitaz"
        chmod +x "/usr/local/bin/unitaz"
        echo -e "${GREEN}Installed 'unitaz' shortcut to /usr/local/bin/unitaz!${NC}"
    fi

    # Reload systemd and enable service
    systemctl daemon-reload
    systemctl enable "${SERVICE_NAME}"
    systemctl restart "${SERVICE_NAME}"
    sleep 1

    echo -e "\n${BOLD}${CYAN}Setting up Admin Credentials...${NC}"
    reset_admin_credentials

    read -rp "Install and configure fail2ban intrusion protection now? [Y/n]: " f2b_choice
    if [[ ! "$f2b_choice" =~ ^[Nn]$ ]]; then
        setup_fail2ban
    fi

    echo -e "\n${GREEN}================================================================${NC}"
    echo -e "${BOLD}${GREEN} Unitaz Relay installed and started successfully!${NC}"
    echo -e " Access Web Panel at: ${CYAN}http://<YOUR-SERVER-IP>:$(get_panel_port)${NC}"
    echo -e "${GREEN}================================================================${NC}"
}

# 3. Web Panel Credentials & Configuration
reset_admin_credentials() {
    echo -e "\n${BOLD}${CYAN}--- Reset Web Panel Admin Credentials ---${NC}"
    read -rp "Enter new Admin Username [admin]: " new_user
    new_user=${new_user:-admin}

    while true; do
        read -s -rp "Enter new Admin Password: " new_pass
        echo
        read -s -rp "Confirm new Admin Password: " confirm_pass
        echo
        if [[ "$new_pass" == "$confirm_pass" ]]; then
            if [[ -z "$new_pass" ]]; then
                echo -e "${RED}Password cannot be empty.${NC}"
                continue
            fi
            if [[ ${#new_pass} -lt 10 ]]; then
                echo -e "${RED}Password must be at least 10 characters long.${NC}"
                continue
            fi
            break
        else
            echo -e "${RED}Passwords do not match. Please try again.${NC}"
        fi
    done

    # Execute admin reset through dotnet if available or config update
    if [[ -f "${INSTALL_DIR}/Netstr.dll" ]]; then
        echo -e "${BLUE}Updating admin credentials in database...${NC}"
        (cd "${INSTALL_DIR}" && dotnet "${INSTALL_DIR}/Netstr.dll" --set-admin-user "$new_user" --set-admin-pass "$new_pass")
    fi

    echo -e "${GREEN}Admin credentials updated for user '${new_user}'!${NC}"
}

configure_network() {
    echo -e "\n${BOLD}${CYAN}--- Network, Port & IP Binding Settings ---${NC}"
    local current_host=$(get_server_host)
    local current_port=$(get_server_port)
    local lan_ip=$(get_local_ip)

    echo -e "Current Listening Host: ${YELLOW}${current_host}${NC}"
    echo -e "Current Listening Port: ${YELLOW}${current_port}${NC}"
    echo -e "Detected Router LAN IP: ${CYAN}${lan_ip}${NC}"
    echo -e ""
    echo -e " ${BOLD}${WHITE}1.${NC} Set Host to 0.0.0.0 (Recommended: All interfaces, enables router/LAN: http://${lan_ip}:${current_port}/admin)"
    echo -e " ${BOLD}${WHITE}2.${NC} Set Host to 127.0.0.1 (Localhost only, disallows router/LAN devices)"
    echo -e " ${BOLD}${WHITE}3.${NC} Set Custom Host IP"
    echo -e " ${BOLD}${WHITE}4.${NC} Change Server Port (current: ${current_port})"
    echo -e " ${BOLD}${WHITE}5.${NC} Configure Both Host & Port"
    echo -e " ${BOLD}${WHITE}0.${NC} Cancel"
    echo -e ""
    read -rp "Select an option [0-5]: " net_choice

    local new_host=""
    local new_port=""

    case "$net_choice" in
        1)
            new_host="0.0.0.0"
            ;;
        2)
            new_host="127.0.0.1"
            ;;
        3)
            read -rp "Enter IP to bind to (e.g. 192.168.88.31): " new_host
            ;;
        4)
            read -rp "Enter new port (1024-65535) [current: ${current_port}]: " new_port
            ;;
        5)
            echo -e "Choose Host IP:"
            echo -e " 1) 0.0.0.0 (All interfaces / Local router: http://${lan_ip})"
            echo -e " 2) 127.0.0.1 (Localhost only)"
            echo -e " 3) Custom IP"
            read -rp "Select [1-3]: " h_sub
            if [[ "$h_sub" == "1" ]]; then new_host="0.0.0.0"
            elif [[ "$h_sub" == "2" ]]; then new_host="127.0.0.1"
            else read -rp "Enter host IP: " new_host; fi
            read -rp "Enter new port (1024-65535) [current: ${current_port}]: " new_port
            ;;
        0)
            return
            ;;
        *)
            echo -e "${RED}Invalid choice.${NC}"
            return
            ;;
    esac

    local config_files=()
    [[ -f "${INSTALL_DIR}/appsettings.json" ]] && config_files+=("${INSTALL_DIR}/appsettings.json")
    [[ -f "${REPO_DIR}/src/Netstr/appsettings.json" ]] && config_files+=("${REPO_DIR}/src/Netstr/appsettings.json")

    if [[ ${#config_files[@]} -eq 0 ]]; then
        echo -e "${RED}Error: appsettings.json not found.${NC}"
        return
    fi

    for cfg in "${config_files[@]}"; do
        if [[ -n "$new_host" ]]; then
            if grep -q '"Host":' "$cfg"; then
                sed -i -E "s/\"Host\":\s*\"[^\"]*\"/\"Host\": \"${new_host}\"/g" "$cfg"
            else
                sed -i -E "s/\"Server\":\s*\{/\"Server\": {\n    \"Host\": \"${new_host}\",/g" "$cfg"
            fi
        fi
        if [[ -n "$new_port" && "$new_port" =~ ^[0-9]+$ ]]; then
            if grep -q '"Port":' "$cfg"; then
                sed -i -E "s/\"Port\":\s*[0-9]+/\"Port\": ${new_port}/g" "$cfg"
            else
                sed -i -E "s/\"Server\":\s*\{/\"Server\": {\n    \"Port\": ${new_port},/g" "$cfg"
            fi
        fi
    done

    echo -e "${GREEN}Configuration updated successfully!${NC}"
    local updated_host=$(get_server_host)
    local updated_port=$(get_server_port)
    echo -e "Now configured to listen on: ${CYAN}http://${updated_host}:${updated_port}${NC}"

    read -rp "Restart unitaz-relay service now to apply? [Y/n]: " restart_choice
    if [[ ! "$restart_choice" =~ ^[Nn]$ ]]; then
        service_restart
    fi
}

change_panel_port() {
    configure_network
}

# 4. System & Network Optimizations
optimize_system() {
    echo -e "\n${BOLD}${CYAN}--- System & High-Concurrency Network Optimizations ---${NC}"
    echo -e "${BLUE}1. Enabling TCP BBR Congestion Control...${NC}"

    if ! grep -q "net.core.default_qdisc = fq" /etc/sysctl.conf 2>/dev/null; then
        echo "net.core.default_qdisc = fq" >> /etc/sysctl.conf
    fi
    if ! grep -q "net.ipv4.tcp_congestion_control = bbr" /etc/sysctl.conf 2>/dev/null; then
        echo "net.ipv4.tcp_congestion_control = bbr" >> /etc/sysctl.conf
    fi

    echo -e "${BLUE}2. Tuning sysctl socket and file limits for C100K WebSockets...${NC}"
    cat >> /etc/sysctl.conf << 'EOF'
# Unitaz high-concurrency network tuning
fs.file-max = 2097152
net.ipv4.tcp_max_syn_backlog = 8192
net.core.somaxconn = 65535
net.ipv4.tcp_tw_reuse = 1
net.ipv4.tcp_fin_timeout = 15
EOF

    sysctl -p > /dev/null 2>&1 || true
    echo -e "${GREEN}System and BBR optimizations applied successfully!${NC}"
}

# 5. Security & Fail2ban
setup_fail2ban() {
    echo -e "\n${BOLD}${CYAN}--- Configure Fail2ban Intrusion Protection ---${NC}"
    if ! command -v fail2ban-client >/dev/null 2>&1; then
        echo -e "${YELLOW}fail2ban is not installed. Installing...${NC}"
        if command -v apt-get >/dev/null 2>&1; then
            apt-get update && apt-get install -y fail2ban
        elif command -v dnf >/dev/null 2>&1; then
            dnf install -y fail2ban
        elif command -v pacman >/dev/null 2>&1; then
            pacman -S --noconfirm fail2ban
        else
            echo -e "${RED}Could not detect package manager. Please install fail2ban manually.${NC}"
            return 1
        fi
    fi

    echo -e "${BLUE}Configuring fail2ban filter for Unitaz Relay...${NC}"
    mkdir -p /etc/fail2ban/filter.d /etc/fail2ban/jail.d

    cat > /etc/fail2ban/filter.d/unitaz-relay.conf << 'EOF'
[Definition]
failregex = ^.*Admin login failed for user '.*' from IP <HOST>.*$
            ^.*Admin Nostr login failed for pubkey '.*' from IP <HOST>.*$
ignoreregex =
EOF

    local s_port=$(get_server_port)
    local log_dir="${INSTALL_DIR}/logs"
    mkdir -p "$log_dir"

    echo -e "${BLUE}Configuring fail2ban jail (port ${s_port}, 5 retries / 10m -> 1h ban)...${NC}"
    cat > /etc/fail2ban/jail.d/unitaz-relay.local << EOF
[unitaz-relay]
enabled = true
port = ${s_port},80,443
filter = unitaz-relay
logpath = ${INSTALL_DIR}/logs/log*.txt
backend = auto
maxretry = 5
findtime = 600
bantime = 3600
action = %(action_)s
EOF

    echo -e "${BLUE}Enabling and starting fail2ban service...${NC}"
    systemctl enable fail2ban >/dev/null 2>&1 || true
    systemctl restart fail2ban >/dev/null 2>&1 || true
    sleep 1

    if systemctl is-active --quiet fail2ban; then
        echo -e "${GREEN}Fail2ban protection for Unitaz Relay configured and active!${NC}"
    else
        echo -e "${YELLOW}Fail2ban configured. Check 'systemctl status fail2ban' if service did not start.${NC}"
    fi
}

# 6. Database tools
backup_database() {
    echo -e "\n${BOLD}${CYAN}--- Backup PostgreSQL Database ---${NC}"
    ensure_docker_postgres
    read -rp "Enter PostgreSQL database name [Netstr]: " db_name
    db_name=${db_name:-Netstr}
    local backup_dir="/var/backups/unitaz"
    mkdir -p "$backup_dir"
    local timestamp=$(date +"%Y%m%d_%H%M%S")
    local backup_file="${backup_dir}/unitaz_backup_${timestamp}.sql.gz"

    echo -e "${BLUE}Creating compressed dump to ${backup_file}...${NC}"
    if command -v pg_dump >/dev/null 2>&1; then
        pg_dump -U postgres "$db_name" | gzip > "$backup_file"
        echo -e "${GREEN}Backup created successfully: ${backup_file} (${YELLOW}$(du -h "$backup_file" | awk '{print $1}')${GREEN})${NC}"
    else
        echo -e "${RED}pg_dump command not found on host. Please ensure postgresql-client is installed.${NC}"
    fi
}

# 7. Uninstall
uninstall_unitaz() {
    echo -e "\n${RED}${BOLD}WARNING: This will completely stop and remove Unitaz Relay service!${NC}"
    read -rp "Are you sure you want to proceed? [y/N]: " confirm
    if [[ "$confirm" =~ ^[Yy]$ ]]; then
        echo -e "${BLUE}Stopping and disabling service...${NC}"
        systemctl stop "${SERVICE_NAME}" 2>/dev/null || true
        systemctl disable "${SERVICE_NAME}" 2>/dev/null || true
        rm -f "${SYSTEMD_FILE}"
        rm -f "/usr/local/bin/unitaz"
        systemctl daemon-reload
        echo -e "${YELLOW}Service uninstalled. Note: Data directory (${INSTALL_DIR}) was preserved.${NC}"
    else
        echo -e "${CYAN}Uninstall cancelled.${NC}"
    fi
}

# Main Interactive Menu
show_menu() {
    while true; do
        print_banner
        echo -e " ${BOLD}${WHITE}1.${NC} Start Relay"
        echo -e " ${BOLD}${WHITE}2.${NC} Stop Relay"
        echo -e " ${BOLD}${WHITE}3.${NC} Restart Relay"
        echo -e " ${BOLD}${WHITE}4.${NC} Detailed Service Status"
        echo -e " ${BOLD}${WHITE}5.${NC} View Live Logs (journalctl)"
        echo -e "${CYAN}----------------------------------------------------------------${NC}"
        echo -e " ${BOLD}${WHITE}6.${NC} Install / Rebuild Unitaz Service"
        echo -e " ${BOLD}${WHITE}7.${NC} Reset Web Panel Admin Credentials"
        echo -e " ${BOLD}${WHITE}8.${NC} Network & Port Settings (Host IP, Port, LAN/Router access)"
        echo -e " ${BOLD}${WHITE}9.${NC} Optimize System & TCP BBR"
        echo -e " ${BOLD}${WHITE}10.${NC} Configure Fail2ban Intrusion Protection"
        echo -e " ${BOLD}${WHITE}11.${NC} Backup PostgreSQL Database"
        echo -e " ${BOLD}${WHITE}12.${NC} Uninstall Unitaz Relay"
        echo -e "${CYAN}----------------------------------------------------------------${NC}"
        echo -e " ${BOLD}${WHITE}0.${NC} Exit"
        echo -e "${CYAN}================================================================${NC}"
        read -rp " Select an option [0-12]: " choice

        case "$choice" in
            1) service_start; read -rp "Press Enter to continue..." ;;
            2) service_stop; read -rp "Press Enter to continue..." ;;
            3) service_restart; read -rp "Press Enter to continue..." ;;
            4) service_status_details; read -rp "Press Enter to continue..." ;;
            5) view_live_logs ;;
            6) install_unitaz_relay; read -rp "Press Enter to continue..." ;;
            7) reset_admin_credentials; read -rp "Press Enter to continue..." ;;
            8) configure_network; read -rp "Press Enter to continue..." ;;
            9) optimize_system; read -rp "Press Enter to continue..." ;;
            10) setup_fail2ban; read -rp "Press Enter to continue..." ;;
            11) backup_database; read -rp "Press Enter to continue..." ;;
            12) uninstall_unitaz; read -rp "Press Enter to continue..." ;;
            0) echo -e "\n${GREEN}Goodbye!${NC}"; exit 0 ;;
            *) echo -e "${RED}Invalid option!${NC}"; sleep 1 ;;
        esac
    done
}

check_root
show_menu
