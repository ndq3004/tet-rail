CREATE TABLE IF NOT EXISTS booking.shard_epochs (
  mapping_version varchar(16) NOT NULL,
  partition_id integer NOT NULL,
  epoch bigint NOT NULL,
  PRIMARY KEY (mapping_version, partition_id)
);
