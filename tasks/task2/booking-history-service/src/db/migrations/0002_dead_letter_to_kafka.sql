-- Сообщения, которые не удалось обработать, теперь уходят в топик booking-events.dlq.
-- Таблицу удаляем, только если в ней не осталось неразобранных сообщений.
do $$ begin
    if exists (select 1 from dead_letter) then
        raise exception 'dead_letter не пуста: разберите сообщения до перехода на топик booking-events.dlq';
    end if;
end $$;
drop table dead_letter;
