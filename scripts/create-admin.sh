#!/bin/bash

# Interactive script to create a SuperAdmin user via the API
# Usage: ./create-admin.sh

set -e

# Color codes for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Default values
DEFAULT_API_URL="http://localhost:5338"

# Function to print colored messages
print_info() {
    echo -e "${BLUE}ℹ${NC} $1"
}

print_success() {
    echo -e "${GREEN}✅${NC} $1"
}

print_error() {
    echo -e "${RED}❌${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

print_header() {
    echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
    echo -e "${CYAN}$1${NC}"
    echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
}

# Function to prompt for input
prompt_input() {
    local prompt=$1
    local default=$2
    local var_name=$3

    if [ -n "$default" ]; then
        read -p "$(echo -e ${CYAN}${prompt}${NC} [${default}]: )" input
        eval "$var_name=\"${input:-$default}\""
    else
        while true; do
            read -p "$(echo -e ${CYAN}${prompt}${NC}: )" input
            if [ -n "$input" ]; then
                eval "$var_name=\"$input\""
                break
            else
                print_error "This field is required!"
            fi
        done
    fi
}

# Print header
clear
print_header "   SuperAdmin Creation Tool   "
echo ""
print_info "This script will create a new SuperAdmin user for your application."
echo ""

# Prompt for email
while true; do
    prompt_input "Enter email address" "" EMAIL

    # Validate email format
    if [[ "$EMAIL" =~ ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$ ]]; then
        break
    else
        print_error "Invalid email format. Please try again."
        echo ""
    fi
done

# Prompt for first name (optional)
echo ""
read -p "$(echo -e ${CYAN}Enter first name${NC} [optional]: )" FIRST_NAME

# Prompt for last name (optional)
echo ""
read -p "$(echo -e ${CYAN}Enter last name${NC} [optional]: )" LAST_NAME

# Prompt for API URL
echo ""
prompt_input "Enter API URL" "$DEFAULT_API_URL" API_URL

# Build JSON payload
JSON_PAYLOAD=$(cat <<EOF
{
  "email": "$EMAIL"
EOF
)

if [ -n "$FIRST_NAME" ]; then
    JSON_PAYLOAD+=",
  \"firstName\": \"$FIRST_NAME\""
fi

if [ -n "$LAST_NAME" ]; then
    JSON_PAYLOAD+=",
  \"lastName\": \"$LAST_NAME\""
fi

JSON_PAYLOAD+="
}"

# Display summary and confirm
echo ""
echo ""
print_header "   Summary   "
echo ""
echo "  Email:      $EMAIL"
[ -n "$FIRST_NAME" ] && echo "  First Name: $FIRST_NAME" || echo "  First Name: (not provided)"
[ -n "$LAST_NAME" ] && echo "  Last Name:  $LAST_NAME" || echo "  Last Name:  (not provided)"
echo "  API URL:    $API_URL"
echo ""

# Ask for confirmation
read -p "$(echo -e ${CYAN}Create SuperAdmin with these details?${NC} [Y/n]: )" CONFIRM
CONFIRM=${CONFIRM:-Y}

if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
    print_warning "Operation cancelled by user"
    exit 0
fi

echo ""
print_info "Creating SuperAdmin user..."
echo ""

# Make the API request
ENDPOINT="$API_URL/api/auth/create-super-admin"
print_info "Sending request to: $ENDPOINT"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$ENDPOINT" \
    -H "Content-Type: application/json" \
    -d "$JSON_PAYLOAD")

# Extract HTTP status code and response body
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')

# Handle response
if [ "$HTTP_CODE" -eq 201 ] || [ "$HTTP_CODE" -eq 200 ]; then
    print_success "SuperAdmin created successfully!"
    echo ""
    echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
    echo ""
    print_info "Next steps:"
    echo "  1. Use the email address to send an OTP: POST /api/auth/send-otp"
    echo "  2. Verify the OTP to authenticate: POST /api/auth/verify-otp"
    echo ""
elif [ "$HTTP_CODE" -eq 400 ]; then
    print_error "Bad Request - Invalid input"
    echo ""
    echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
    exit 1
elif [ "$HTTP_CODE" -eq 409 ]; then
    print_warning "SuperAdmin already exists"
    echo ""
    echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
    exit 1
elif [ "$HTTP_CODE" -eq 000 ]; then
    print_error "Failed to connect to API at $API_URL"
    print_info "Make sure the API is running and accessible"
    exit 1
else
    print_error "Request failed with status code: $HTTP_CODE"
    echo ""
    echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
    exit 1
fi
