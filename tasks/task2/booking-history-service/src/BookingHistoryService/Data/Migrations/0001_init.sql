-- История бронирований: строка на каждое событие. event_id — ключ идемпотентности, повторная доставка
-- историю не меняет. Следующие события того же бронирования (отмена, изменение) лягут отдельными строками.
create table booking_history (
    event_id            uuid           primary key,
    event_type          text           not null,
    booking_id          bigint         not null,
    user_id             text           not null,
    hotel_id            text           not null,
    promo_code          text,
    discount_amount     numeric(10, 2) not null,
    price               numeric(10, 2) not null,
    booked_at           timestamptz    not null,
    booked_at_estimated boolean        not null,  -- дата перенесённого бронирования неизвестна
    source              text           not null,  -- booking-service | migration
    correlation_id      text           not null,
    received_at         timestamptz    not null default now()
);
create index booking_history_booking_idx on booking_history (booking_id);

-- Сообщения, которые нельзя обработать: сохраняются с причиной для разбора и не останавливают поток.
create table dead_letter (
    id              bigint generated always as identity primary key,
    topic           text        not null,
    kafka_partition integer     not null,
    kafka_offset    bigint      not null,
    payload         text,
    error           text        not null,
    correlation_id  text,
    received_at     timestamptz not null default now(),
    unique (topic, kafka_partition, kafka_offset)
);

create view stats_by_user as
select user_id, count(*) as bookings, sum(price) as revenue, max(booked_at) as last_booking_at
from booking_history where event_type = 'BookingCreated' group by user_id;

create view stats_by_hotel as
select hotel_id, count(*) as bookings, sum(price) as revenue,
       count(*) filter (where promo_code is not null) as with_promo
from booking_history where event_type = 'BookingCreated' group by hotel_id;

create view stats_by_day as
select (booked_at at time zone 'UTC')::date as day, count(*) as bookings, sum(price) as revenue,
       count(*) filter (where booked_at_estimated) as estimated_dates
from booking_history where event_type = 'BookingCreated' group by 1;
