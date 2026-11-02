-- Аналитик читает историю ролью только на чтение и не трогает боевые БД.
-- Выполняется при каждом migrate; пароль задаёт SchemaMigrations.cs отдельным параметром.
do $$ begin
  if not exists (select from pg_roles where rolname = 'analyst') then
    create role analyst login;
  end if;
end $$;
grant usage on schema public to analyst;
grant select on booking_history, stats_by_user, stats_by_hotel, stats_by_day to analyst;
