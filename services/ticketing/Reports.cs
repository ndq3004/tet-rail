using Npgsql;

namespace TetRail.Ticketing;

public static class Reports
{
    public static async Task<IResult> ReadAsync(NpgsqlDataSource database, CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            WITH orders AS (
                SELECT DISTINCT ON (body->>'order_id') body,occurred_at FROM reporting.events
                WHERE kind='OrderCreated.v1' ORDER BY body->>'order_id',occurred_at
            ), paid AS (
                SELECT body->>'order_id' AS id,min(occurred_at) AS paid_at
                FROM reporting.events WHERE kind='PaymentSucceeded.v1' GROUP BY body->>'order_id'
            ), sales AS (
                SELECT orders.body->>'currency' AS currency,sum((orders.body->>'total_amount')::numeric) AS revenue,
                    count(*) AS paid_orders
                FROM orders JOIN paid ON orders.body->>'order_id'=paid.id
                GROUP BY orders.body->>'currency'
            )
            SELECT jsonb_build_object(
                'sales',coalesce((SELECT jsonb_agg(to_jsonb(sales)) FROM sales),'[]'::jsonb),
                'conversion',jsonb_build_object('orders',(SELECT count(*) FROM orders),'paid',(SELECT count(*) FROM paid)),
                'payment',coalesce((SELECT jsonb_object_agg(kind,total) FROM (
                    SELECT kind,count(*) AS total FROM reporting.events WHERE kind IN
                    ('PaymentSucceeded.v1','PaymentFailed.v1','LatePaymentReceived.v1','PaymentReviewRequired.v1','OrderExpired.v1')
                    GROUP BY kind) counts),'{}'::jsonb),
                'checkoutLatencyP99Ms',(SELECT percentile_cont(0.99) WITHIN GROUP
                    (ORDER BY extract(epoch FROM (paid.paid_at-orders.occurred_at))*1000)
                    FROM orders JOIN paid ON orders.body->>'order_id'=paid.id WHERE paid.paid_at>=orders.occurred_at),
                'occupancy',coalesce((SELECT jsonb_agg(to_jsonb(counts)) FROM (
                    SELECT trip_id,status,count(*) AS segments FROM reporting.occupancy
                    WHERE status IN ('HELD','SOLD') GROUP BY trip_id,status) counts),'[]'::jsonb),
                'activeHoldSegments',(SELECT count(*) FROM reporting.occupancy WHERE status='HELD'),
                'lag',coalesce((SELECT jsonb_agg(to_jsonb(s)) FROM reporting.streams s),'[]'::jsonb),
                'deadLetters',jsonb_build_object(
                    'projection',(SELECT count(*) FROM reporting.events WHERE kind='ProjectionDeadLetter.v1'),
                    'delivery',(SELECT count(*) FROM ticketing.jobs WHERE dead_at IS NOT NULL))
            )::text
            """);
        return Results.Content((string?)await command.ExecuteScalarAsync(ct), "application/json");
    }
}
