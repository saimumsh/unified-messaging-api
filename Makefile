.PHONY: db db-stop api connector migrate test install

DB_NAME := unified-messaging-postgres

db:  ## Start PostgreSQL (tries `docker compose`, falls back to `docker run`)
	@docker compose up -d 2>/dev/null || docker run -d --name $(DB_NAME) \
		-e POSTGRES_USER=unified -e POSTGRES_PASSWORD=unified -e POSTGRES_DB=unified_messaging \
		-p 5432:5432 postgres:17-alpine || docker start $(DB_NAME)

db-stop:
	@docker compose down 2>/dev/null || docker stop $(DB_NAME)

install:
	cd whatsapp-connector && npm install

migrate:
	cd backend/UnifiedMessaging.Api && dotnet ef database update

api:  ## Run the .NET unified API on :5080
	cd backend/UnifiedMessaging.Api && dotnet run

connector:  ## Run the Node/Baileys WhatsApp connector on :3001
	cd whatsapp-connector && npm start

test:
	dotnet test
