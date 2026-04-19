// Emits three structured log events via winston + @datalust/winston-seq against the URL / API key
// passed through env. Used by the WinstonSeqCompatTests .NET test to verify our raw-ingestion
// endpoint handles seq-logging's JSON envelope wire format.

import winston from "winston";
import { SeqTransport } from "@datalust/winston-seq";

const url = process.env.SEQ_URL;
const apiKey = process.env.SEQ_API_KEY;
if (!url || !apiKey) {
	console.error("SEQ_URL and SEQ_API_KEY env vars are required");
	process.exit(2);
}

const transport = new SeqTransport({
	serverUrl: url,
	apiKey,
	onError: (e) => {
		console.error("winston-seq error:", e);
	},
	handleExceptions: true,
	handleRejections: true,
});

const logger = winston.createLogger({
	level: "verbose",
	format: winston.format.combine(
		winston.format.errors({ stack: true }),
		winston.format.json(),
	),
	transports: [transport],
});

logger.info("hello from {Source} attempt {Attempt}", { Source: "winston-compat", Attempt: 1 });
logger.warn("disk {Device} at {Percent}%", { Device: "/dev/sda1", Percent: 92 });
logger.error("explosion {Code}", { Code: 42, stack: "Error: boom\n    at test" });

// seq-logging batches; close() initiates flush but returns before HTTP completes. We must await
// the transport's flush promise explicitly — otherwise bun exits before the POST round-trips.
await transport.flush();
