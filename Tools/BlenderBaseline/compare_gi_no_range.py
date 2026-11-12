"""Compare an independent Native NoRange bake against frozen Cycles room data.

Native and Cycles UVs are mapped independently from the same source-local UV2.
No old manifest is rewritten and no Unity/Blender process is used.
"""
import argparse
from datetime import datetime, timezone
import csv
import json
import math
from pathlib import Path

import numpy as np

from compare_gi_whitebox import sha, load_json, bilinear, metrics, interior_room, preview, save_png, exr_channels, Y


def native_uv(face,record,fractions):
    uv=np.asarray(face['uv2'],dtype=np.float64)
    local=uv[0]+fractions[:,0,None]*(uv[1]-uv[0])+fractions[:,1,None]*(uv[3]-uv[0])
    st=np.asarray([record['scaleOffset'][key] for key in 'xyzw'])
    return local*st[:2]+st[2:]


def cycles_uv(face,fractions):
    uv=np.asarray(face['atlasUV'],dtype=np.float64)
    return uv[0]+fractions[:,0,None]*(uv[1]-uv[0])+fractions[:,1,None]*(uv[3]-uv[0])


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--reference',type=Path,required=True)
    parser.add_argument('--native-report',type=Path,required=True)
    parser.add_argument('--cycles',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    reference=args.reference.resolve();native_report=args.native_report.resolve();cycles_path=args.cycles.resolve();out=args.output.resolve()
    out.mkdir(parents=True,exist_ok=True)
    manifest=load_json(reference/'manifest.json');native=load_json(native_report);state=load_json(cycles_path/'cycles_state.json')
    calibration=load_json(cycles_path/'light_unit_calibration.json')
    if not all(native.get(key) for key in ('valid','restored','protectedHashesUnchanged')) or native.get('nativeOverlaps')!=0:
        raise ValueError('Independent Native bake did not pass ownership/overlap/source preservation checks')
    if native.get('falloff')!='InverseSquaredNoRangeAttenuation':raise ValueError('Native light is not NoRange')
    if native['sourceScene']!=manifest['scene']:raise ValueError('Native copy comes from another source scene')
    if not state.get('completed') or not calibration.get('valid') or state['source_manifest_sha256']!=sha(reference/'manifest.json'):
        raise ValueError('Frozen Cycles source or calibration is invalid')
    if state['pass_filter']!=['INDIRECT'] or state['receiver_color_included'] or state['denoise'] or state['margin']!=0:
        raise ValueError('Cycles is not uncolored, unfiltered artificial indirect reference')
    expected_intensity=manifest['pointLight']['candela']/manifest['nativeIntensityScale']
    if not math.isclose(native['injectedCandelaOverBakeScale'],expected_intensity,rel_tol=1e-6):
        raise ValueError('Native point intensity differs from the original Cycles numerical embedding')
    records={f['renderer']:f for f in native['faces']}
    if set(records)!={f['renderer'] for f in manifest['faces']}:raise ValueError('Native/Cycles receiver set differs')
    protected={str(p):sha(p) for p in (reference/'manifest.json',reference/'source_native_validation.json',native_report,cycles_path/'cycles_state.json',cycles_path/'light_unit_calibration.json')}
    st_errors=[]
    for face in manifest['faces']:
        record=records[face['renderer']]
        if record['mesh']!=face['meshAsset'] or record.get('nativeOverlap'):
            raise ValueError('Mesh identity or overlap differs: '+face['renderer'])
        n=np.array([record['worldNormal'][k] for k in 'xyz'])
        if not np.allclose(n,face['normalsWS'][0],rtol=0,atol=1e-6):raise ValueError('World normal changed')
        st=np.array([record['scaleOffset'][k] for k in 'xyzw'])
        st_errors.append(float(np.abs(st-face['scaleOffset']).max()))
    native_textures={};cycle_textures={}
    for atlas in native['atlases']:
        folder=native_report.parent;h,w=atlas['height'],atlas['width']
        for key in ('color','direction'):
            path=folder/atlas[key+'Raw'];digest=sha(path)
            if digest!=atlas[key+'SHA256']:raise ValueError('New Native GPU bytes changed')
            protected[str(path)]=digest
        color=np.fromfile(folder/atlas['colorRaw'],dtype='<f2').reshape(h,w,4).astype(np.float64)
        channels=atlas['directionNativeChannels']
        direction=np.fromfile(folder/atlas['directionRaw'],dtype='u1').reshape(h,w,channels).astype(np.float64)/255
        if channels==3:direction=np.concatenate((direction,np.ones((h,w,1))),axis=2)
        elif channels!=4:raise ValueError('Unsupported Native direction channels')
        if not np.isfinite(color).all() or (color[:,:,:3]<0).any():raise ValueError('Invalid Native values')
        native_textures[atlas['index']]=(color,direction)
    for atlas in manifest['atlases']:
        record=next(a for a in state['atlases'] if a['index']==atlas['index'])
        for path in (cycles_path/record['raw'],cycles_path/record['exr'],reference/atlas['colorRaw'],reference/atlas['directionRaw']):
            protected[str(path)]=sha(path)
        if sha(cycles_path/record['raw'])!=record['rawSHA256']:raise ValueError('Frozen Cycles bytes changed')
        if sha(reference/atlas['colorRaw'])!=atlas['colorSHA256'] or sha(reference/atlas['directionRaw'])!=atlas['directionSHA256']:
            raise ValueError('Old Native source bytes changed')
        if any(exr_channels(cycles_path/record['exr']).get(k)!=2 for k in ('R','G','B')):raise ValueError('Cycles EXR is not float32')
        cycle_textures[atlas['index']]=np.fromfile(cycles_path/record['raw'],dtype='<f4').reshape(atlas['height'],atlas['width'],4).astype(np.float64)
    rows=[];faces=[];rooms={key:[[],[],[]] for key in ('A','B','outside')}
    for face in manifest['faces']:
        record=records[face['renderer']];color,direction=native_textures[record['atlas']];cycles=cycle_textures[face['atlas']]
        p=np.array(face['positionsWS']);normal=np.asarray(face['normalsWS'][0],dtype=float);normal/=np.linalg.norm(normal)
        fractions=[(.5,.5)]+[(.25+.5*(x+.5)/8,.25+.5*(y+.5)/8) for y in range(8) for x in range(8)]
        if face['renderer'] in ('Floor_PY','Roof_NY','OuterBack_NZ'):
            edges=np.column_stack((p[1]-p[0],p[3]-p[0]))
            for room_x in (-2.1,2.1):
                point=p.mean(axis=0);point[0]=room_x
                q=np.linalg.lstsq(edges,point-p[0],rcond=None)[0]
                if (q>0).all() and (q<1).all():fractions.append(tuple(q))
        fractions=np.asarray(fractions)
        n_uv=native_uv(face,record,fractions);c_uv=cycles_uv(face,fractions)
        n=bilinear(color,n_uv);d=bilinear(direction,n_uv);c=bilinear(cycles,c_uv)
        positions=p[0]+fractions[:,0,None]*(p[1]-p[0])+fractions[:,1,None]*(p[3]-p[0])
        classification=interior_room(positions,normal)
        factor=np.maximum((d[:,:3]-.5)@normal+.5,0)/np.maximum(d[:,3],1e-4)
        e_over_pi=n[:,:3]/math.pi;geom_over_pi=e_over_pi*factor[:,None]
        for k,room in enumerate(classification):
            if room!='outside' and c[k,3]<.99:raise ValueError('Invalid Cycles interior sample coverage')
            row={'renderer':face['renderer'],'sample':k,'room':room,'nativeAtlas':record['atlas'],'cyclesAtlas':face['atlas'],
                 'nativeU':float(n_uv[k,0]),'nativeV':float(n_uv[k,1]),'cyclesU':float(c_uv[k,0]),'cyclesV':float(c_uv[k,1]),
                 'worldX':float(positions[k,0]),'worldY':float(positions[k,1]),'worldZ':float(positions[k,2]),
                 'nativeRawE':float(n[k,:3]@Y),'nativeEOverPi':float(e_over_pi[k]@Y),
                 'nativeGeomEOverPi':float(geom_over_pi[k]@Y),'directionFactor':float(factor[k]),'cyclesEOverPi':float(c[k,:3]@Y)}
            rows.append(row);rooms[room][0].append(e_over_pi[k]);rooms[room][1].append(geom_over_pi[k]);rooms[room][2].append(c[k,:3])
        mask=classification!='outside'
        faces.append({'renderer':face['renderer'],'centerRoom':str(classification[0]),'nativeAtlas':record['atlas'],'cyclesAtlas':face['atlas'],
                      'oldST':face['scaleOffset'],'newST':[record['scaleOffset'][k] for k in 'xyzw'],
                      'centerNativeRawE':float(n[0,:3]@Y),'centerNativeGeomEOverPi':float(geom_over_pi[0]@Y),
                      'centerCyclesEOverPi':float(c[0,:3]@Y),'centerDirectionFactor':float(factor[0]),
                      'interiorNoDirection':metrics(e_over_pi[mask],c[mask,:3]),'interiorGeomDirectional':metrics(geom_over_pi[mask],c[mask,:3])})
    room_results={room:{'nativeRawDivPi':metrics(values[0],values[2]),'nativeGeomDivPi':metrics(values[1],values[2])} for room,values in rooms.items()}
    report={'utc':datetime.now(timezone.utc).isoformat(),'referenceManifest':str(reference/'manifest.json'),'referenceManifestSHA256':sha(reference/'manifest.json'),
            'cyclesFolder':str(cycles_path),'cyclesStateSHA256':sha(cycles_path/'cycles_state.json'),'cyclesNativeOriginalRunId':state['nativeRunId'],
            'nativeNewReport':str(native_report),'nativeNewReportSHA256':sha(native_report),'nativeNewRunId':native['runId'],
            'nativeCopiedScene':native['scene'],'nativeFalloff':native['falloff'],'nativeRangeMetres':native['lightRange'],
            'nativeInjectedIOverK':native['injectedCandelaOverBakeScale'],'expectedReferenceIOverK':expected_intensity,
            'lightIntensityAndNoRangeAligned':True,'sameGeometryByMeshNormalSetAndProtectedSource':True,
            'stMapping':'Separate Native localUV2*newST and Cycles old atlasUV. No source manifest mutation or assumed equal ST.',
            'maxOldNewSTAbsoluteDifference':max(st_errors),'changedSTValuesAbove1eMinus10':sum(v>1e-10 for v in st_errors),
            'nativeRawDomain':'irradiance E','nativeEToEOverPi':1/math.pi,'conversionIsFitted':False,
            'receiverDiffuseNativeToPipeline':.01/math.pi,'cyclesEOverPiToPipeline':.01,
            'nativeFilter':'Auto (same source whitebox settings)','cyclesFilter':'no denoising, no margin; per-texel MC area samples',
            'fullyIdenticalEstimatorClaimed':False,'sampling':'Mip0 bilinear at shared local points; room means are equal-weight sample means, not a world-area energy integral.',
            'roomMetrics':room_results,'faces':faces,'protectedSourceSHA256':protected,
            'limitations':['Native Auto filtering and Cycles no denoising differ; per-pixel distribution need not be identical.',
                           'Both max bounce settings=4, but truncation semantics for repeated room transport have not been proven identical.',
                           'No claim about final PBR material, dynamic sky energy, temporal filtering, screen MIP selection or mobile performance.']}
    crop_names=['OuterWest_PX','OuterFrontA_Lower_PZ','OuterFrontA_Upper_PZ','Partition_Upper_NX','OuterEast_NX','OuterFrontB_PZ','Partition_Upper_PX']
    values=[];crop_pairs=[];axis=(np.arange(256)+.5)/256*.95+.025;yy,xx=np.meshgrid(axis,axis,indexing='ij');fractions=np.column_stack((xx.ravel(),yy.ravel()))
    for name in crop_names:
        face=next(f for f in manifest['faces'] if f['renderer']==name);record=records[name]
        n=bilinear(native_textures[record['atlas']][0],native_uv(face,record,fractions))[:,:3].reshape(256,256,3)/math.pi
        c=bilinear(cycle_textures[face['atlas']],cycles_uv(face,fractions))[:,:3].reshape(256,256,3)
        crop_pairs.append((name,n,c));values.extend((n.ravel(),c.ravel()))
    positive=np.concatenate(values);positive=positive[positive>0];gain=.7/max(float(np.quantile(positive,.99)),1e-12)
    contact=[]
    for name,n,c in crop_pairs:
        pair=np.concatenate((preview(n,gain)[::-1],np.full((256,8,3),220,dtype='u1'),preview(c,gain)[::-1]),axis=1)
        save_png(out/(name+'_native_norange_cycles.png'),pair);contact.extend((pair,np.full((8,pair.shape[1],3),220,dtype='u1')))
    save_png(out/'interior_wall_contact_sheet.png',np.concatenate(contact,axis=0))
    report['preview']={'gain':gain,'left':'new Native NoRange / pi','right':'frozen Cycles2048 E/pi','faceOrder':crop_names,'file':'interior_wall_contact_sheet.png'}
    changed=[path for path,digest in protected.items() if sha(Path(path))!=digest]
    if changed:raise ValueError('Read-only comparison detected changed source data: '+str(changed))
    report['protectedHashesUnchanged']=True
    (out/'comparison.json').write_text(json.dumps(report,indent=2,allow_nan=False),encoding='utf-8')
    with (out/'uv_points.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    lines=['New Native Open NoRange vs frozen Cycles2048','Native run='+native['runId'],'Original Cycles source run='+state['nativeRunId'],
           'NoRange and light intensity aligned. Native E divided by theoretical pi once, no fitted scale.',
           'Native AutoFilter and Cycles NoFilter remain different; this is not full estimator equivalence.',
           'Max old/new ST difference='+str(max(st_errors))+'; both UV mappings evaluated separately.',
           'Room | nativeNg E/pi | Cycles E/pi | native/Cycles | relative mean delta']
    for room in ('A','B'):
        value=room_results[room]['nativeGeomDivPi'];ratio=value['nativeToCycles']
        lines.append(f"{room} | {value['nativeMean']:.12g} | {value['cyclesMean']:.12g} | {ratio:.9g} | {(ratio-1):.6%}")
    lines.append('Sources SHA256 unchanged; face details and per-point native/cycles UVs recorded.')
    (out/'RESULTS.txt').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    html=['<!doctype html><html><meta charset="utf-8"><title>Open NoRange Native / Cycles</title><style>body{font:16px sans-serif;margin:32px}img{max-width:100%}table{border-collapse:collapse}td,th{padding:6px;border:1px solid #bbb;text-align:right}</style>',
          '<h1>Open NoRange · Native / Cycles</h1><pre>'+'\n'.join(lines)+'</pre>',
          '<p>Shared gain; left Native NoRange / pi, right Cycles2048. Native Auto filtering versus Cycles unfiltered remains different. All old data untouched.</p>']
    for name,_,_ in crop_pairs:html.append('<h2>'+name+'</h2><img src="'+name+'_native_norange_cycles.png">')
    html.append('<h2>Actual face center distribution</h2><table><tr><th>Face</th><th>Room</th><th>Native Ng E/pi</th><th>Cycles E/pi</th><th>Native/Cycles</th></tr>')
    for f in faces:
        if f['centerRoom']=='outside':continue
        ratio=f['centerNativeGeomEOverPi']/f['centerCyclesEOverPi'] if f['centerCyclesEOverPi']>1e-12 else None
        html.append('<tr><td>'+f['renderer']+'</td><td>'+f['centerRoom']+'</td><td>'+format(f['centerNativeGeomEOverPi'],'.8g')+'</td><td>'+format(f['centerCyclesEOverPi'],'.8g')+'</td><td>'+str(ratio)+'</td></tr>')
    html.append('</table><p>Exact hashes, separate STs and all surface samples: comparison.json / uv_points.csv.</p></html>')
    (out/'comparison.html').write_text('\n'.join(html),encoding='utf-8')
    print('\n'.join(lines))


if __name__=='__main__':main()
