-- One database per service; no service reads another's (ADR-0001).
CREATE DATABASE catalogdb;
CREATE DATABASE bookingdb;
CREATE DATABASE paymentsdb;
CREATE DATABASE notificationsdb;
