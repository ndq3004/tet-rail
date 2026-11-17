CREATE TABLE IF NOT EXISTS booking.payment_confirmations (
    transaction_id uuid PRIMARY KEY,
    order_id uuid NOT NULL UNIQUE,
    result jsonb NOT NULL
);
