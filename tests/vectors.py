import hashlib
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def det(label, n):
    out = b""
    i = 0
    while len(out) < n:
        out += hashlib.sha256(f"{label}{i}".encode()).digest(); i += 1
    return out[:n]

# --- Nintendo XTS: big-endian sector number as the tweak input ---
xts_key = det("xtskey", 32)
plain = det("xtsplain", 0x600)
first_sector = 5
ct = b""
for s in range(len(plain)//0x200):
    tweak = (first_sector + s).to_bytes(16, "big")
    enc = Cipher(algorithms.AES(xts_key), modes.XTS(tweak)).encryptor()
    ct += enc.update(plain[s*0x200:(s+1)*0x200]) + enc.finalize()

# --- Nintendo CTR: nonce (8) || (offset>>4) big-endian ---
ctr_key = det("ctrkey", 16)
nonce = det("ctrnonce", 8)
ctr_plain = det("ctrplain", 0x123)
offset = 0x4007
block = offset >> 4
skip = offset & 0xF
iv = nonce + block.to_bytes(8, "big")
enc = Cipher(algorithms.AES(ctr_key), modes.CTR(iv)).encryptor()
ks_input = b"\0"*skip + ctr_plain
ctr_ct = (enc.update(ks_input) + enc.finalize())[skip:]

def h(b): return b.hex().upper()
print("XTS_KEY", h(xts_key)); print("XTS_SHA_PLAIN", hashlib.sha256(plain).hexdigest())
print("XTS_SHA_CT", hashlib.sha256(ct).hexdigest().upper())
print("CTR_KEY", h(ctr_key)); print("CTR_NONCE", h(nonce))
print("CTR_SHA_CT", hashlib.sha256(ctr_ct).hexdigest().upper())
print("CTR_CT_FIRST16", h(ctr_ct[:16]))
