# usb_uart_bridge.py
# Minimal USB <-> UART bridge for RP2040 (MicroPython)
# Usage: import usb_uart_bridge; usb_uart_bridge.main(baud=38400, uart_id=0)

import sys, time
from machine import UART

def main(baud=38400, uart_id=0):
    rs232 = UART(0, baudrate=38400,tx=0,rx=1,invert=0|1)
    ttl=UART(1,baudrate=115200,tx=8,rx=9)
    #stdin_buf = getattr(ttl.read(1), "buffer", None)
    #stdout_buf = getattr(sys.stdout, "buffer", None)

    while True:
        # ttl -> rs232
        try:
            if ttl.any():
                b = ttl.read(1)  # raw byte if available
                if b:
                    rs232.write(b)
        except Exception:
            # ignore read errors, continue loop
            pass

        # rs232 -> ttl
        try:
            if ttl.any():
                data = rs232.read()
                if data:
                    # prefer raw write to stdout.buffer if available
                    if data:
                        ttl.write(data)
                        ttl.flush()
                    else:
                        # decode with latin-1 to preserve byte values when writing as str
                        ttl.write(data.decode('latin-1'))
                        ttl.flush()
        except Exception:
            pass

        time.sleep_ms(5)

# allow running as script
if __name__ == "__main__":
    main()